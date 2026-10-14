import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { fakeImportServer, householdPersons, session, SESSION_ID } from '@/test/importServer';
import { nidFile, upload, workedExample } from '@/test/nidFile';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./ImportLayout'); await import('./ImportHome'); }, 30_000);

describe('Import › home', () => {
  it('lists recent imports with their state and a way back into each', async () => {
    const fake = fakeImportServer({
      sessions: [
        session({ state: 'open' }),
        session({ id: '0192aaaa-0000-7000-8000-000000000002', state: 'committed', createdAt: '2026-09-20T09:00:00+00:00' }),
      ],
    });
    server.use(...fake.handlers);
    const { container } = await renderApp('/import');

    expect(await screen.findByRole('heading', { name: 'Imports récents' })).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /en cours/ })).toHaveTextContent('Reprendre');
    expect(screen.getByRole('row', { name: /importé/ })).toHaveTextContent('Voir');
    await expectNoAxeViolations(container);
  });

  it('shows what the local check finds, and nothing is sent', async () => {
    const fake = fakeImportServer();
    server.use(...fake.handlers);
    const { user } = await renderApp('/import');

    await upload(user, nidFile('{"format":"nid.data","version":1,"documents":[],\n"persons":[}'));
    expect(await screen.findByRole('alert')).toHaveTextContent("Le fichier n'est pas du JSON valide (ligne 2, colonne 12).");

    await upload(user, nidFile({ format: 'nid.data', version: 1, documents: [], persons: [] }));
    expect(await screen.findByRole('alert')).toHaveTextContent("La section documents n'est pas encore importable.");
    expect(fake.calls).toEqual([]);
  });

  it('summarizes a readable file, and another file can be chosen', async () => {
    server.use(...fakeImportServer().handlers);
    const { user, container } = await renderApp('/import');

    await upload(user, nidFile(workedExample, 'foyer.nid.json'));
    expect(await screen.findByRole('heading', { name: 'Contrôle local' })).toBeInTheDocument();
    expect(screen.getByText('Fichier : foyer.nid.json')).toBeInTheDocument();
    expect(screen.getByText('persons : 2')).toBeInTheDocument();
    expect(screen.getByText('subscriptions : 1')).toBeInTheDocument();
    expect(screen.getByText('Lots à envoyer : 3.')).toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Choisir un autre fichier' }));
    expect(await screen.findByLabelText('Choisir un fichier')).toBeInTheDocument();
  });

  it('takes a dropped file', async () => {
    server.use(...fakeImportServer().handlers);
    await renderApp('/import');

    const zone = (await screen.findByLabelText('Choisir un fichier')).closest('[data-drop-zone]');
    if (!zone) throw new Error('no drop zone');
    fireEvent.drop(zone, { dataTransfer: { files: [nidFile()] } });
    await waitFor(() => expect(screen.getByText('Lots à envoyer : 3.')).toBeInTheDocument());
  });
});

describe('Import › adults and start', () => {
  const [alice, bob] = householdPersons;
  /** The field exists before the household persons arrive; its proposal only once they have. */
  const proposed = (label: string, id: string) => waitFor(() => expect(screen.getByLabelText(label)).toHaveValue(id));
  const startEnabled = () => waitFor(() => expect(screen.getByRole('button', { name: "Commencer l'import" })).toBeEnabled());

  it('proposes the household adult with the same email, and no one otherwise', async () => {
    server.use(...fakeImportServer().handlers);
    const { user, container } = await renderApp('/import');

    await upload(user, nidFile());
    await proposed('Adulte 1 (adult-1)', alice!.id);
    expect(screen.getByLabelText('Adulte 2 (adult-2)')).toHaveValue('');
    // Only adults are offered: a dependent is never an account.
    expect(screen.queryByRole('option', { name: /Léo/ })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('matches the email whatever its case', async () => {
    server.use(...fakeImportServer().handlers);
    const { user } = await renderApp('/import');

    const file = { ...workedExample, persons: [{ ref: 'moi', kind: 'adult', display_name: 'Moi', email: 'ALICE@Example.test' }] };
    await upload(user, nidFile(file));
    await proposed('Moi (moi)', alice!.id);
  });

  it('starts the session with the adult map and opens it', async () => {
    const fake = fakeImportServer();
    server.use(...fake.handlers);
    const { user, router } = await renderApp('/import');

    await upload(user, nidFile());
    await proposed('Adulte 1 (adult-1)', alice!.id);
    await user.selectOptions(screen.getByLabelText('Adulte 2 (adult-2)'), bob!.id);
    await user.click(screen.getByRole('button', { name: "Commencer l'import" }));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/import/${SESSION_ID}`));
    expect(fake.started).toEqual([{ format: 'nid.data', version: 1, adults: { 'adult-1': alice!.id, 'adult-2': bob!.id } }]);
    await waitFor(() => expect(fake.calls).toContain('validate'));
  });

  it('leaves an unmapped adult out of the map, for the server to report', async () => {
    const fake = fakeImportServer();
    server.use(...fake.handlers);
    const { user, router } = await renderApp('/import');

    await upload(user, nidFile());
    await startEnabled();
    await user.click(screen.getByRole('button', { name: "Commencer l'import" }));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/import/${SESSION_ID}`));
    expect(fake.started).toEqual([{ format: 'nid.data', version: 1, adults: { 'adult-1': alice!.id } }]);
    await waitFor(() => expect(fake.calls).toContain('validate'));
  });

  it('says when the file has no adult to map', async () => {
    server.use(...fakeImportServer().handlers);
    const { user } = await renderApp('/import');

    await upload(user, nidFile({ ...workedExample, persons: [{ ref: 'kid', kind: 'dependent', display_name: 'Léo' }] }));
    expect(await screen.findByText('Le fichier ne contient aucun adulte à associer.')).toBeInTheDocument();
  });

  it('offers the import just started when another start is refused', async () => {
    const fake = fakeImportServer({ oneActive: true });
    server.use(...fake.handlers);
    const { user, router } = await renderApp('/import');

    // The recent list is read (empty) before the import starts.
    await screen.findByLabelText('Choisir un fichier');
    await waitFor(() => expect(screen.queryByText('Chargement')).not.toBeInTheDocument());
    await upload(user, nidFile());
    await startEnabled();
    await user.click(screen.getByRole('button', { name: "Commencer l'import" }));
    expect(await screen.findByRole('heading', { name: 'Rapport de validation' })).toBeInTheDocument();

    await act(async () => {
      await router.navigate({ to: '/import' });
    });
    await user.click(await screen.findByRole('button', { name: "Commencer l'import" }));
    expect(await screen.findByText("Un import est déjà en cours. Reprenez-le ou annulez-le avant d'en commencer un autre.")).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: "Reprendre l'import en cours" })).toHaveAttribute('href', `/import/${SESSION_ID}`);
    expect(fake.calls.filter((c) => c === 'start')).toHaveLength(2);
  });

  it('offers the import in progress when one is already active', async () => {
    const fake = fakeImportServer({ sessionActive: true, sessions: [session({ state: 'validated' })] });
    server.use(...fake.handlers);
    const { user, router } = await renderApp('/import');

    await upload(user, nidFile());
    await startEnabled();
    await user.click(screen.getByRole('button', { name: "Commencer l'import" }));

    expect(await screen.findByText("Un import est déjà en cours. Reprenez-le ou annulez-le avant d'en commencer un autre.")).toBeInTheDocument();
    await user.click(await screen.findByRole('link', { name: "Reprendre l'import en cours" }));
    expect(router.state.location.pathname).toBe(`/import/${SESSION_ID}`);
  });
});
