import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities, allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';

type Adult = components['schemas']['AdultView'];

// aliceSession (the default renderApp() session) is this same person: these tests exercise the self rows.
// The menu is a lazy chunk: its real trigger (aria-expanded) replaces the plain name once loaded.
const openerReady = () => screen.findByRole('button', { name: 'Alice Dev', expanded: false });
const alice: Adult = { id: '01926b9e-0000-7000-8000-00000000000a', displayName: 'Alice Dev', email: 'alice@example.test', status: 'active', isAdmin: true };
const bob: Adult = { id: '01926b9e-0000-7000-8000-00000000000b', displayName: 'Bob', email: 'bob@example.test', status: 'active', isAdmin: false };
const carol: Adult = { id: '01926b9e-0000-7000-8000-00000000000c', displayName: 'Carol', email: 'carol@example.test', status: 'pending', isAdmin: false };

function serve(adults: Adult[], maxAdults = 3) {
  let reads = 0;
  server.use(
    http.get('*/api/v1/admin/users', () => { reads += 1; return HttpResponse.json(adults); }),
    http.get('*/api/v1/admin/household', () => HttpResponse.json({ maxAdults, referenceCurrency: 'EUR', adultCount: adults.length })),
  );
  return { reads: () => reads };
}

describe('Administration › Utilisateurs', () => {
  it('lists the adults with their status and their role', async () => {
    serve([alice, carol]);
    const { container } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    const carolRow = await screen.findByRole('row', { name: /Carol/ });
    expect(within(carolRow).getByText('en attente')).toBeInTheDocument();
    expect(within(screen.getByRole('row', { name: /Alice Dev/ })).getByText('admin')).toBeInTheDocument();
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
    expect(within(aliceRow).getByText('admin')).toBeInTheDocument();
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
    await user.click(within(carolRow).getByRole('button', { name: "Annuler l'invitation" }));
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

    expect(await screen.findByRole('heading', { name: "Vous n'avez pas le droit de faire cette action." })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Administration' })).not.toBeInTheDocument();
  });

  it('an admin finds the admin entry in the navigation', async () => {
    serve([alice]);
    const { user } = await renderApp('/subscriptions', { capabilities: adminCapabilities });

    await user.click(await openerReady());
    expect(await screen.findByRole('menuitem', { name: 'Administration' })).toBeInTheDocument();
  });

  it('warns the admin distinctly before it signs itself out everywhere', async () => {
    serve([alice, bob]);
    const { user } = await renderApp('/admin/users', { capabilities: adminCapabilities });

    const aliceRow = await screen.findByRole('row', { name: /Alice Dev/ });
    await user.click(within(aliceRow).getByRole('button', { name: 'Déconnecter partout' }));

    // The confirmation loads lazily: it may not be there yet when the click returns.
    expect(await screen.findByText(/vous devrez vous reconnecter/)).toBeInTheDocument();
  });
});
