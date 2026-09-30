import { act, cleanup, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { canonicalizeText } from '@/lib/canonical-json';
import { sha256Hex } from '@/lib/hash';
import { expectNoAxeViolations } from '@/test/axe';
import { fakeImportServer, SESSION_ID, session } from '@/test/importServer';
import { nidFile, upload, workedExample } from '@/test/nidFile';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

describe('Import › one session', () => {
  it('shows an expired import and starts over', async () => {
    server.use(...fakeImportServer({ sessions: [session({ state: 'expired' })] }).handlers);
    const { user, router, container } = await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText('Cet import a expiré. Recommencez avec le fichier.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Recommencer' }));
    expect(router.state.location.pathname).toBe('/import');
  });

  it('treats an active import past its expiry as expired, as the server does', async () => {
    server.use(...fakeImportServer({ sessions: [session({ state: 'validated', expiresAt: '2020-01-01T00:00:00+00:00' })] }).handlers);
    await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText('Cet import a expiré. Recommencez avec le fichier.')).toBeInTheDocument();
    expect(screen.getByText('État : expiré')).toBeInTheDocument();
  });

  it('shows a cancelled import', async () => {
    server.use(...fakeImportServer({ sessions: [session({ state: 'cancelled' })] }).handlers);
    await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText('Cet import a été annulé.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Commencer un nouvel import' })).toBeInTheDocument();
  });

  it('cancels an open import without its file', async () => {
    const fake = fakeImportServer({ sessions: [session({ state: 'open' })] });
    server.use(...fake.handlers);
    const { user, router } = await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByRole('heading', { name: "Reprendre l'import" })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: "Annuler l'import" }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/import'));
    expect(fake.calls).toEqual(['cancel']);
  });

  it('says an unknown import is not found, as for another person’s', async () => {
    server.use(...fakeImportServer().handlers);
    await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByRole('alert')).toHaveTextContent('Cet élément est introuvable.');
  });
});

describe('Import › sending', () => {
  const open = () => fakeImportServer({ sessions: [session({ state: 'open' })] });

  it('resumes an open import: the same file sends the same batches again, then validates', async () => {
    const fake = open();
    server.use(...fake.handlers);
    const { user, container } = await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByRole('heading', { name: "Reprendre l'import" })).toBeInTheDocument();
    await expectNoAxeViolations(container);
    await upload(user, nidFile());

    await waitFor(() => expect(fake.calls).toEqual(['put persons/0', 'put subscriptions/0', 'put billing_phases/0', 'validate']));
    for (const batch of fake.received) {
      expect(batch.contentType).toBe('application/json');
      const entities = batch.raw.slice(batch.raw.indexOf(',"entities":') + ',"entities":'.length, -1);
      expect(await sha256Hex(canonicalizeText(entities))).toBe(batch.hash);
    }

    // A second time with the same file: the same slots and hashes, which the server replays (§11.6).
    const first = fake.received.map((b) => [b.section, b.index, b.hash]);
    cleanup(); // the tab is closed: the prepared file is gone with it
    fake.sessions.get(SESSION_ID)!.state = 'open';
    fake.received.length = 0;
    const again = await renderApp(`/import/${SESSION_ID}`);
    await upload(again.user, nidFile());
    await waitFor(() => expect(fake.received.map((b) => [b.section, b.index, b.hash])).toEqual(first));
  });

  it('announces progress in a live region', async () => {
    // Slow answers, so the send step is still on screen when the test looks at it.
    server.use(...fakeImportServer({ sessions: [session({ state: 'open' })], putDelayMs: 100 }).handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile());
    const status = await screen.findByRole('status');
    expect(status).toHaveAttribute('aria-live', 'polite');
    await waitFor(() => expect(status).toHaveTextContent('Lots envoyés : 3 sur 3'));
  });

  it('retries a batch the server failed to answer', async () => {
    const fake = fakeImportServer({ sessions: [session({ state: 'open' })], failPuts: 1 });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile());
    await waitFor(() => expect(fake.calls).toContain('validate'), { timeout: 5000 });
    expect(fake.calls.filter((c) => c === 'put persons/0')).toHaveLength(2);
  });

  it('pauses between batches and continues', async () => {
    const fake = fakeImportServer({ sessions: [session({ state: 'open' })], putDelayMs: 150 });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile());
    await user.click(await screen.findByRole('button', { name: 'Mettre en pause' }));
    expect(await screen.findByText('Envoi en pause.')).toBeInTheDocument();
    expect(fake.calls).toEqual(['put persons/0']);

    await user.click(screen.getByRole('button', { name: "Reprendre l'envoi" }));
    await waitFor(() => expect(fake.calls).toContain('validate'));
    expect(fake.calls.filter((c) => c.startsWith('put'))).toHaveLength(3);
  });

  it('shows the refused batches with their paths, and « Corriger » cancels the import', async () => {
    const issue = { severity: 'error' as const, code: 'import.invalid_value', message: 'The name is empty.', path: 'subscriptions[0].name' };
    const fake = fakeImportServer({ sessions: [session({ state: 'open' })], refuse: (s) => (s === 'subscriptions' ? [issue] : null) });
    server.use(...fake.handlers);
    const { user, router } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile());
    expect(await screen.findByText('Certains lots ont été refusés. Corrigez le fichier puis recommencez.')).toBeInTheDocument();
    expect(screen.getByText('subscriptions[0].name')).toBeInTheDocument();
    expect(screen.getByText('The name is empty.')).toBeInTheDocument();
    expect(fake.calls).not.toContain('validate');

    await user.click(screen.getByRole('button', { name: 'Corriger' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/import'));
    expect(fake.calls).toContain('cancel');
  });

  it('shows a cancelled import as cancelled in the recent list, not as still in progress', async () => {
    const issue = { severity: 'error' as const, code: 'import.invalid_value', message: 'The name is empty.', path: 'subscriptions[0].name' };
    const fake = fakeImportServer({ sessions: [session({ state: 'open' })], refuse: (s) => (s === 'subscriptions' ? [issue] : null) });
    server.use(...fake.handlers);
    const { user } = await renderApp('/import');

    await user.click(await screen.findByRole('link', { name: 'Reprendre' }));
    await upload(user, nidFile());
    await user.click(await screen.findByRole('button', { name: 'Corriger' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    expect(await screen.findByRole('row', { name: /annulé/ })).toHaveTextContent('Voir');
    expect(screen.queryByRole('row', { name: /en cours/ })).not.toBeInTheDocument();
  });

  it('says so when the file is not the one of this import', async () => {
    const fake = fakeImportServer({
      sessions: [session({ state: 'open' })],
      failWith: (s) => (s === 'persons' ? { status: 409, code: 'import.slot_taken', title: "Batch 0 of 'persons' was already received with other content." } : null),
    });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile());
    expect(await screen.findByText("Ce fichier n'est pas celui de cet import : un lot a déjà été reçu avec un autre contenu.")).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Corriger' })).toBeInTheDocument();
    expect(fake.calls).toEqual(['put persons/0']);
  });

  it('lets another file be chosen when the file is not the one of this import, and keeps the import', async () => {
    const fake = fakeImportServer({
      sessions: [session({ state: 'open' })],
      failWith: (s) => (s === 'persons' ? { status: 409, code: 'import.slot_taken', title: "Batch 0 of 'persons' was already received with other content." } : null),
    });
    server.use(...fake.handlers);
    const { user, router } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile());
    await user.click(await screen.findByRole('button', { name: 'Choisir un autre fichier' }));
    expect(await screen.findByRole('heading', { name: "Reprendre l'import" })).toBeInTheDocument();
    expect(screen.getByLabelText('Choisir un fichier')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(`/import/${SESSION_ID}`);
    expect(fake.calls).toEqual(['put persons/0']);
  });

  it('stops at once on a 4xx and shows the server’s reason', async () => {
    const fake = fakeImportServer({
      sessions: [session({ state: 'open' })],
      failWith: (s) => (s === 'providers' ? { status: 422, code: 'import.section_not_supported', title: "The section 'providers' is not supported yet." } : null),
    });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile({ ...workedExample, providers: [{ key: 'maison', name: 'Maison' }] }));
    expect(await screen.findByText("The section 'providers' is not supported yet.")).toBeInTheDocument();
    // Not retried, and no later batch was sent: the loop had stopped before the page said so.
    expect(fake.calls).toEqual(['put persons/0', 'put providers/0']);
  });

  it('validates again when the import changed during validation', async () => {
    const fake = fakeImportServer({ sessions: [session({ state: 'open' })], validateStale: 1 });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile());
    expect(await screen.findByText("L'import a changé pendant la validation.")).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Valider de nouveau' }));
    await waitFor(() => expect(fake.calls.filter((c) => c === 'validate')).toHaveLength(2));
  });

  it('shows the import’s own view when validation finds it no longer open', async () => {
    const fake = fakeImportServer({ sessions: [session({ state: 'open' })], validateStale: 1 });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByRole('heading', { name: "Reprendre l'import" })).toBeInTheDocument();
    fake.sessions.get(SESSION_ID)!.state = 'expired'; // it expires while the batches are on their way
    await upload(user, nidFile());
    expect(await screen.findByText('Cet import a expiré. Recommencez avec le fichier.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Valider de nouveau' })).not.toBeInTheDocument();
    expect(fake.calls.filter((c) => c === 'validate')).toHaveLength(1);
  });

  it('stops sending when the import is cancelled mid-send', async () => {
    const fake = fakeImportServer({ sessions: [session({ state: 'open' })], putDelayMs: 150 });
    server.use(...fake.handlers);
    const { user, router } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile());
    await waitFor(() => expect(fake.calls).toEqual(['put persons/0']));
    await user.click(screen.getByRole('button', { name: "Annuler l'import" }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/import'));

    // Longer than the remaining batches' PUTs would take: a loop still running would have sent more by now.
    await new Promise((resolve) => setTimeout(resolve, 500));
    const cancelled = fake.calls.indexOf('cancel');
    expect(cancelled).toBeGreaterThan(0);
    expect(fake.calls.slice(cancelled + 1)).toEqual([]);
    expect(fake.calls).not.toContain('validate');
  });

  it('stops sending when the page is left, so a return does not race a second send', async () => {
    const fake = fakeImportServer({ sessions: [session({ state: 'open' })], putDelayMs: 100 });
    server.use(...fake.handlers);
    const { user, router } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile());
    await waitFor(() => expect(fake.calls).toEqual(['put persons/0']));

    await act(async () => {
      await router.navigate({ to: '/import' });
    });
    // Longer than the remaining batches' PUTs would take: a loop still running would have sent more by now.
    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(fake.calls).toEqual(['put persons/0']);

    await act(async () => {
      await router.navigate({ to: '/import/$id', params: { id: SESSION_ID } });
    });
    await waitFor(() => expect(fake.calls).toContain('validate'));
    // The abandoned loop sent exactly its one in-flight batch, then stopped; the new one sends every batch once,
    // so no slot is ever in flight from two loops at the same time.
    expect(fake.calls).toEqual(['put persons/0', 'put persons/0', 'put subscriptions/0', 'put billing_phases/0', 'validate']);
  });
});

describe('Import › report, confirmation and commit', () => {
  const counts = (created: number, unchanged = 0, conflict = 0, replaced = 0) => ({ created, unchanged, conflict, replaced });

  it('goes from the file to the final report', async () => {
    const fake = fakeImportServer();
    server.use(...fake.handlers);
    const { user, container } = await renderApp('/import');

    await upload(user, nidFile());
    await waitFor(() => expect(screen.getByRole('button', { name: "Commencer l'import" })).toBeEnabled());
    await user.click(screen.getByRole('button', { name: "Commencer l'import" }));

    expect(await screen.findByRole('heading', { name: 'Rapport de validation' })).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /subscriptions/ })).toHaveTextContent('1');
    expect(screen.getByText('Aucune erreur ni avertissement.')).toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Importer' }));
    expect(await screen.findByRole('heading', { name: 'Import terminé' })).toBeInTheDocument();
    expect(fake.calls.at(-1)).toBe('commit');
    expect(screen.getByRole('link', { name: 'Voir les abonnements' })).toHaveAttribute('href', '/subscriptions');
    await expectNoAxeViolations(container);
  });

  it.each([
    ['created envelopes', { envelopes: counts(2) }, true],
    ['replaced envelopes', { envelopes: counts(0, 0, 0, 1) }, true],
    ['envelopes neither created nor replaced', { envelopes: counts(0, 3) }, false],
    ['no envelopes section', { subscriptions: counts(1) }, false],
  ])('links to the investments after an import with %s only when some were made', async (_name, sections, linked) => {
    const report = { state: 'committed', sections, issues: [] };
    server.use(...fakeImportServer({ sessions: [session({ state: 'committed', report })] }).handlers);
    const { container } = await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByRole('heading', { name: 'Import terminé' })).toBeInTheDocument();
    if (linked) {
      expect(screen.getByRole('link', { name: 'Voir les placements' })).toHaveAttribute('href', '/investments');
    } else {
      expect(screen.queryByRole('link', { name: 'Voir les placements' })).not.toBeInTheDocument();
    }
    expect(screen.getByRole('link', { name: 'Voir les abonnements' })).toHaveAttribute('href', '/subscriptions');
    await expectNoAxeViolations(container);
  });

  it('opens a validated import without the file, and imports it', async () => {
    const report = { state: 'validated', sections: { persons: counts(2), subscriptions: counts(1) }, issues: [] };
    const fake = fakeImportServer({ sessions: [session({ state: 'validated', report })] });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await user.click(await screen.findByRole('button', { name: 'Importer' }));
    expect(await screen.findByRole('heading', { name: 'Import terminé' })).toBeInTheDocument();
  });

  it('blocks the import on a conflict alongside another error, and « Corriger » cancels it', async () => {
    const conflict = { severity: 'error' as const, code: 'import.conflict', message: 'sub-spotify was imported with other content.', path: 'subscriptions[0]' };
    const other = { severity: 'error' as const, code: 'import.invalid_value', message: 'y', path: 'subscriptions[1].name' };
    const report = { state: 'invalid', sections: { subscriptions: counts(0, 0, 1) }, issues: [conflict, other] };
    const fake = fakeImportServer({ sessions: [session({ state: 'invalid', report })] });
    server.use(...fake.handlers);
    const { user, router } = await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText("Des entités existent déjà avec un autre contenu : l'import ne peut pas continuer. Corrigez le fichier.")).toBeInTheDocument();
    expect(screen.getByText('subscriptions[0]')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Importer' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Corriger' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/import'));
  });

  it('lets warnings through', async () => {
    const warning = { severity: 'warning' as const, code: 'core.fx_rate_missing', message: 'No USD rate on 2024-01-05.', path: 'purchases[3]' };
    const report = { state: 'validated', sections: { purchases: counts(4) }, issues: [warning] };
    server.use(...fakeImportServer({ sessions: [session({ state: 'validated', report })] }).handlers);
    await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByRole('heading', { name: 'Avertissements (1)' })).toBeInTheDocument();
    expect(screen.getByText('No USD rate on 2024-01-05.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Importer' })).toBeInTheDocument();
  });

  it('shows why a commit was not written', async () => {
    const report = { state: 'validated', sections: { subscriptions: counts(1) }, issues: [] };
    const failure = { severity: 'error' as const, code: 'import.apply_failed', message: 'The database refused the write.', path: '' };
    server.use(...fakeImportServer({ sessions: [session({ state: 'validated', report })], commitFails: [failure] }).handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await user.click(await screen.findByRole('button', { name: 'Importer' }));
    expect(await screen.findByText("L'import n'a pas pu être écrit. Le rapport ci-dessous en donne la raison.")).toBeInTheDocument();
    expect(await screen.findByText('The database refused the write.')).toBeInTheDocument();
    expect(screen.getByText('fichier entier')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Importer' })).not.toBeInTheDocument();
  });

  it('downloads the final report', async () => {
    const report = { state: 'committed', sections: { subscriptions: counts(1) }, issues: [] };
    server.use(...fakeImportServer({ sessions: [session({ state: 'committed', report })] }).handlers);
    const blobs: Blob[] = [];
    const createObjectURL = vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => { blobs.push(blob as Blob); return 'blob:report'; });
    const revokeObjectURL = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const downloads: string[] = [];
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { downloads.push(this.download); });
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await user.click(await screen.findByRole('button', { name: 'Télécharger le rapport' }));
    expect(downloads).toEqual([`nid-import-${SESSION_ID}.json`]);
    expect(JSON.parse(await blobs[0]!.text())).toMatchObject({ id: SESSION_ID, report });
    await waitFor(() => expect(revokeObjectURL).toHaveBeenCalledWith('blob:report'));
    [createObjectURL, revokeObjectURL, click].forEach((spy) => spy.mockRestore());
  });

  it('offers to replace when conflicts are the only errors, validates again with replace, then imports with it', async () => {
    const conflict = { severity: 'error' as const, code: 'import.conflict', message: 'sub-spotify was imported with other content.', path: 'subscriptions[0]' };
    const invalid = { state: 'invalid', sections: { subscriptions: counts(0, 0, 1) }, issues: [conflict] };
    const replacing = { state: 'validated', sections: { subscriptions: counts(0, 0, 0, 1) }, issues: [] };
    const fake = fakeImportServer({ sessions: [session({ state: 'invalid', report: invalid })], validation: replacing });
    server.use(...fake.handlers);
    const { user, container } = await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText('1 élément a déjà été importé avec un autre contenu.')).toBeInTheDocument();
    const box = screen.getByRole('checkbox', { name: 'Remplacer ces éléments par le contenu du fichier' });
    await expectNoAxeViolations(container);
    await user.click(box);

    await waitFor(() => expect(fake.bodies.validate.at(-1)).toEqual({ replace: true }));
    expect(await screen.findByRole('columnheader', { name: 'Remplacés' })).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Importer' }));
    await waitFor(() => expect(fake.bodies.commit.at(-1)).toEqual({ replace: true }));
  });

  it('offers no replacement when another error is there', async () => {
    const conflict = { severity: 'error' as const, code: 'import.conflict', message: 'x', path: 'subscriptions[0]' };
    const other = { severity: 'error' as const, code: 'import.invalid_value', message: 'y', path: 'subscriptions[1].name' };
    const report = { state: 'invalid', sections: { subscriptions: counts(0, 0, 1) }, issues: [conflict, other] };
    server.use(...fakeImportServer({ sessions: [session({ state: 'invalid', report })] }).handlers);
    await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText('subscriptions[1].name')).toBeInTheDocument();
    expect(screen.queryByRole('checkbox', { name: /Remplacer/ })).not.toBeInTheDocument();
  });

  it('shows the audit entry of a committed import', async () => {
    const report = { state: 'committed', sections: { subscriptions: counts(0, 0, 0, 1) }, issues: [] };
    const audit = { committedAt: '2026-09-28T10:00:00Z', contentHash: 'a'.repeat(64), format: 'nid.data', version: 1, replace: true };
    server.use(...fakeImportServer({ sessions: [session({ state: 'committed', report, audit })] }).handlers);
    const { container } = await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText('aaaaaaaaaaaa…')).toBeInTheDocument();
    expect(screen.getByText('nid.data, version 1')).toBeInTheDocument();
    expect(screen.getByText('Remplacement : oui')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('reverts the checkbox and shows the problem when a replace validation fails, then clears it on a successful retry', async () => {
    const conflict = { severity: 'error' as const, code: 'import.conflict', message: 'sub-spotify was imported with other content.', path: 'subscriptions[0]' };
    const invalid = { state: 'invalid', sections: { subscriptions: counts(0, 0, 1) }, issues: [conflict] };
    const fake = fakeImportServer({ sessions: [session({ state: 'invalid', report: invalid })], validateStale: 1 });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    const box = await screen.findByRole('checkbox', { name: 'Remplacer ces éléments par le contenu du fichier' });
    await user.click(box);

    expect(await screen.findByText('Cet élément a changé entre-temps. Rechargez la page pour voir sa dernière version.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Remplacer ces éléments par le contenu du fichier' })).not.toBeChecked());

    await user.click(screen.getByRole('checkbox', { name: 'Remplacer ces éléments par le contenu du fichier' }));
    await waitFor(() => expect(fake.bodies.validate).toHaveLength(2));
    await waitFor(() => expect(screen.queryByText('Cet élément a changé entre-temps. Rechargez la page pour voir sa dernière version.')).not.toBeInTheDocument());
  });

  it('keeps the checkbox visible and checked, letting it be unchecked, when a replace validation answers another error', async () => {
    const conflict = { severity: 'error' as const, code: 'import.conflict', message: 'sub-spotify was imported with other content.', path: 'subscriptions[0]' };
    const invalid = { state: 'invalid', sections: { subscriptions: counts(0, 0, 1) }, issues: [conflict] };
    const notPermitted = { severity: 'error' as const, code: 'import.not_permitted', message: 'Vous ne pouvez pas modifier ceci.', path: 'subscriptions[0]' };
    const refused = { state: 'invalid', sections: { subscriptions: counts(0, 0, 1) }, issues: [notPermitted] };
    const fake = fakeImportServer({ sessions: [session({ state: 'invalid', report: invalid })], validation: refused });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    const box = await screen.findByRole('checkbox', { name: 'Remplacer ces éléments par le contenu du fichier' });
    await user.click(box);

    expect(await screen.findByText('Vous ne pouvez pas modifier ceci.')).toBeInTheDocument();
    const boxAgain = screen.getByRole('checkbox', { name: 'Remplacer ces éléments par le contenu du fichier' });
    expect(boxAgain).toBeChecked();

    await user.click(boxAgain);
    await waitFor(() => expect(fake.bodies.validate.at(-1)).toEqual({ replace: false }));
  });
});
