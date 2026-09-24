import { cleanup, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
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

  it('stops at once on a 4xx and shows the server’s reason', async () => {
    const fake = fakeImportServer({
      sessions: [session({ state: 'open' })],
      failWith: (s) => (s === 'providers' ? { status: 422, code: 'import.section_not_supported', title: "The section 'providers' is not supported yet." } : null),
    });
    server.use(...fake.handlers);
    const { user } = await renderApp(`/import/${SESSION_ID}`);

    await upload(user, nidFile({ ...workedExample, providers: [{ key: 'maison', name: 'Maison' }] }));
    expect(await screen.findByText("The section 'providers' is not supported yet.")).toBeInTheDocument();
    expect(fake.calls.filter((c) => c === 'put providers/0')).toHaveLength(1);
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
});
