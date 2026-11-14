import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities, aliceSession, allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';

type Adult = components['schemas']['AdultView'];

// aliceSession (the default renderApp() session) is this same person: these tests exercise the self rows.
const alice: Adult = { id: '01926b9e-0000-7000-8000-00000000000a', displayName: 'Alice Dev', email: 'alice@example.test', status: 'active', isAdmin: true, methods: ['oidc'], totpEnabled: false };
const bob: Adult = { id: '01926b9e-0000-7000-8000-00000000000b', displayName: 'Bob', email: 'bob@example.test', status: 'active', isAdmin: false, methods: ['oidc'], totpEnabled: false };
const carol: Adult = { id: '01926b9e-0000-7000-8000-00000000000c', displayName: 'Carol', email: 'carol@example.test', status: 'pending', isAdmin: false, methods: [], totpEnabled: false };

function serve(adults: Adult[], maxAdults = 3) {
  let reads = 0;
  server.use(
    http.get('*/api/v1/admin/users', () => { reads += 1; return HttpResponse.json(adults); }),
    http.get('*/api/v1/admin/household', () => HttpResponse.json({ maxAdults, referenceCurrency: 'EUR', adultCount: adults.length })),
  );
  return { reads: () => reads };
}

describe('Administration › Utilisateurs', () => {
  it('has the Administration breadcrumb', async () => {
    serve([alice]);
    await renderApp('/admin/users', { capabilities: adminCapabilities });
    expect(screen.queryByRole('navigation', { name: "Sections de l'administration" })).toBeNull();
    expect(within(screen.getByRole('navigation', { name: 'Fil d’Ariane' })).getByRole('link', { name: 'Administration' })).toHaveAttribute('href', '/admin');
    expect(await screen.findByRole('heading', { level: 1, name: 'Utilisateurs' })).toBeInTheDocument();
  });

  it('lists the adults with their status and their role', async () => {
    serve([alice, carol]);
    const { container } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    const carolRow = await screen.findByRole('row', { name: /Carol/ });
    expect(within(carolRow).getByText('En attente')).toBeInTheDocument();
    expect(within(screen.getByRole('row', { name: /Alice Dev/ })).getByText('Actif')).toBeInTheDocument();
    expect(within(screen.getByRole('row', { name: /Alice Dev/ })).getByText('Admin')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('adds an adult from the form, then reloads the list', async () => {
    const counter = serve([alice]);
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/admin/users', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json(carol, { status: 201 });
    }));
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Ajouter un adulte' }));
    await user.type(await screen.findByLabelText('Nom'), 'Carol');
    await user.type(screen.getByLabelText('Email'), 'carol@example.test');
    await user.click(screen.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(sent).toEqual([{ displayName: 'Carol', email: 'carol@example.test' }]));
    await waitFor(() => expect(counter.reads()).toBe(2));
    await waitFor(() => expect(toasts()).toHaveTextContent('Adulte ajouté.'));
  });

  it('keeps the add button in the header and enabled at the cap, and answers the click with the reason', async () => {
    serve([alice, bob], 2);
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    const add = await screen.findByRole('button', { name: 'Ajouter un adulte' });
    expect(add).toBeEnabled();
    expect(add.closest('[data-page-actions]')).not.toBeNull();
    expect(screen.queryByText(/limité à 2 adultes/)).not.toBeInTheDocument();

    await user.click(add);
    expect(screen.getByRole('status')).toHaveTextContent('Le foyer est limité à 2 adultes. Pour en ajouter un, relevez le plafond dans Foyer');
    expect(within(screen.getByRole('status')).getByRole('link', { name: 'Foyer' })).toHaveAttribute('href', '/admin/household');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('leaves the admin role to the identity provider: no button promotes or demotes, and the page says where it comes from', async () => {
    serve([alice, bob]);
    await renderApp('/admin/users', { capabilities: adminCapabilities });

    const aliceRow = await screen.findByRole('row', { name: /Alice Dev/ });
    expect(within(aliceRow).getByText('Admin')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /rôle admin|Rendre admin/ })).not.toBeInTheDocument();
    expect(screen.getByText(/groupes du fournisseur d'identité/)).toBeInTheDocument();
  });

  it('cancels an invitation only once confirmed', async () => {
    serve([alice, carol]);
    const deleted: string[] = [];
    server.use(http.delete('*/api/v1/admin/users/:id', ({ params }) => {
      deleted.push(String(params.id));
      return HttpResponse.json({ id: params.id });
    }));
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    const carolRow = await screen.findByRole('row', { name: /Carol/ });
    await user.click(within(carolRow).getByRole('button', { name: 'Actions de Carol' }));
    await user.click(await screen.findByRole('menuitem', { name: "Annuler l'invitation" }));
    expect(deleted).toEqual([]);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(deleted).toEqual([carol.id]));
  });

  it('shows the problem the server answers', async () => {
    serve([alice]);
    server.use(http.post('*/api/v1/admin/users', () => HttpResponse.json(
      { status: 422, code: 'core.email_taken', title: 'Another adult already has this email address.' }, { status: 422 })));
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Ajouter un adulte' }));
    await user.type(await screen.findByLabelText('Nom'), 'Alice bis');
    await user.type(screen.getByLabelText('Email'), 'alice@example.test');
    await user.click(screen.getByRole('button', { name: 'Ajouter' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Another adult already has this email address.');
  });

  it('a non-admin is sent to the forbidden page and sees no admin entry', async () => {
    await renderApp('/admin/users', { capabilities: allCapabilities });

    expect(await screen.findByRole('heading', { level: 1, name: 'Accès réservé' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Administration' })).not.toBeInTheDocument();
  });

  it('an admin finds the users page in the sidebar', async () => {
    serve([alice]);
    const { user } = await renderApp('/subscriptions', { capabilities: adminCapabilities });

    const sidebar = screen.getByRole('navigation', { name: 'Navigation principale' });
    // The Administration group starts folded off an admin page (issue 142).
    await user.click(within(sidebar).getByRole('button', { name: 'Administration' }));
    expect(within(sidebar).getByRole('link', { name: 'Utilisateurs' })).toHaveAttribute('href', '/admin/users');
  });

  it('warns the admin distinctly before it signs itself out everywhere', async () => {
    serve([alice, bob]);
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    const aliceRow = await screen.findByRole('row', { name: /Alice Dev/ });
    await user.click(within(aliceRow).getByRole('button', { name: 'Actions d’Alice Dev' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Déconnecter partout' }));

    // The confirmation loads lazily: it may not be there yet when the click returns.
    expect(await screen.findByText(/vous devrez vous reconnecter/)).toBeInTheDocument();
  });
});

describe('Administration › Utilisateurs, comptes locaux (issue 215)', () => {
  const local = { ...aliceSession, methods: ['password'] };
  const dave: Adult = { id: '01926b9e-0000-7000-8000-00000000000d', displayName: 'Dave', email: null, status: 'active', isAdmin: false, methods: ['password'],
    lockedUntil: '2026-11-13T18:00:00+00:00', linkExpiresAt: null, totpEnabled: true };

  it('creates a one-time link and shows it once, with its expiry', async () => {
    serve([alice, carol]);
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/admin/users/:id/password-link', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ url: 'https://nid.example.test/password#nid_link_abc', purpose: 'enroll', expiresAt: '2026-11-16T18:00:00+00:00' }, { status: 201 });
    }));
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities, session: local });

    await user.click(await screen.findByRole('button', { name: 'Actions de Carol' }));
    await user.click(await screen.findByRole('menuitem', { name: "Créer son lien d'accès" }));

    const dialog = await screen.findByRole('dialog', { name: 'Lien à usage unique' });
    expect(within(dialog).getByRole('textbox')).toHaveValue('https://nid.example.test/password#nid_link_abc');
    expect(dialog).toHaveTextContent('Carol');
    expect(sent).toEqual([{ revokeTokens: false }]);
    expect(screen.getByText(/Chaque adulte se connecte avec son identifiant/)).toBeInTheDocument();
  });

  it('shows a locked account and unlocks it', async () => {
    serve([alice, dave]);
    let unlocked = false;
    server.use(http.post('*/api/v1/admin/users/:id/unlock', () => { unlocked = true; return HttpResponse.json({ id: dave.id }); }));
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities, session: local });

    expect(within(await screen.findByRole('row', { name: /Dave/ })).getByText('Verrouillé')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Actions de Dave' }));
    expect(screen.getByRole('menuitem', { name: 'Créer un lien de mot de passe' })).toBeInTheDocument();
    await user.click(screen.getByRole('menuitem', { name: 'Déverrouiller' }));

    await waitFor(() => expect(unlocked).toBe(true));
    await waitFor(() => expect(toasts()).toHaveTextContent('Compte déverrouillé.'));
  });

  it('adds an adult without an email', async () => {
    serve([alice]);
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/admin/users', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json(carol, { status: 201 }); }));
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities, session: local });

    await user.click(await screen.findByRole('button', { name: 'Ajouter un adulte' }));
    await user.type(await screen.findByLabelText('Nom'), 'Dave');
    await user.click(screen.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(sent).toEqual([{ displayName: 'Dave', email: '' }]));
  });
});

describe('Administration › Utilisateurs, rôle admin réglé dans Nid (issue 215)', () => {
  const nidDecides = () => server.use(http.get('*/api/v1/admin/system', () => HttpResponse.json({
    version: '1.0.0', startedAt: '2026-09-24T06:00:00+00:00', health: { database: 'healthy', identityProvider: 'not_used' }, databaseSizeBytes: 1000,
    auth: { mode: 'local', adminSource: 'nid', insecureHttp: false },
  })));

  it('gives the role after a confirmation', async () => {
    serve([alice, bob]);
    nidDecides();
    const sent: unknown[] = [];
    server.use(http.put('*/api/v1/admin/users/:id/admin', async ({ request, params }) => {
      sent.push({ id: params.id, body: await request.json() });
      return HttpResponse.json({ ...bob, isAdmin: true });
    }));
    const { user, location } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    expect(await screen.findByText(/Le rôle admin se règle ici/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Actions de Bob' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Donner le rôle admin' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('Bob pourra gérer les utilisateurs');
    await user.click(within(dialog).getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(sent).toEqual([{ id: bob.id, body: { admin: true } }]));
    expect(location.reload).not.toHaveBeenCalled();
  });

  it('reloads the app only once taking the role from oneself has succeeded', async () => {
    serve([alice, bob]);
    nidDecides();
    let calls = 0;
    server.use(http.put('*/api/v1/admin/users/:id/admin', () => {
      calls += 1;
      return calls === 1
        ? HttpResponse.json({ status: 409, code: 'core.last_admin', title: 'The household must keep at least one admin.' }, { status: 409 })
        : HttpResponse.json({ ...alice, isAdmin: false });
    }));
    const { user, location } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    for (let attempt = 0; attempt < 2; attempt++) {
      await user.click(await screen.findByRole('button', { name: 'Actions d’Alice Dev' }));
      await user.click(await screen.findByRole('menuitem', { name: 'Retirer le rôle admin' }));
      const dialog = await screen.findByRole('alertdialog');
      await user.click(within(dialog).getByRole('button', { name: 'Confirmer' }));
      await waitFor(() => expect(calls).toBe(attempt + 1));
    }

    await waitFor(() => expect(location.reload).toHaveBeenCalledTimes(1));
  });

  it('offers no role action when the identity provider decides', async () => {
    serve([alice, bob]);
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Actions de Bob' }));
    expect(screen.queryByRole('menuitem', { name: 'Donner le rôle admin' })).not.toBeInTheDocument();
  });
});
