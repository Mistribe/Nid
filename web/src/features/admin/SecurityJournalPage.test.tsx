import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { expectLabelBefore } from '@/test/filters';
import { renderApp } from '@/test/renderApp';
import { pick } from '@/test/controls';
import { server } from '@/test/server';

const adults = [
  { id: 'a', displayName: 'Alice', email: 'alice@example.test', status: 'active', isAdmin: true },
  { id: 'b', displayName: 'Bob', email: 'bob@example.test', status: 'active', isAdmin: false },
];

describe('Administration › Journal de sécurité', () => {
  it('names the adults and shows the identifiers only', async () => {
    server.use(
      http.get('*/api/v1/admin/users', () => HttpResponse.json(adults)),
      http.get('*/api/v1/admin/security-journal', () => HttpResponse.json({
        items: [
          { seq: 3, at: '2026-09-26T08:00:00+00:00', event: 'session_revoked', actor: { kind: 'person', personId: 'a', via: 'ui' }, subjectPersonId: 'b', details: {} },
          { seq: 2, at: '2026-09-26T07:00:00+00:00', event: 'login_refused', actor: { kind: 'system', personId: null, via: 'job' }, subjectPersonId: null, details: { subject: 'sub-x' } },
        ],
        total: 3, page: 1,
      })),
    );
    const { container } = await renderApp('/admin/security', { capabilities: adminCapabilities });

    const revoked = await screen.findByRole('row', { name: /Sessions fermées/ });
    expect(within(revoked).getByText('Bob')).toBeInTheDocument();
    expect(within(revoked).getByText('Alice')).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /Connexion refusée/ })).toHaveTextContent('sub-x');
    await expectNoAxeViolations(container);
  });

  // 1F4 design §4: what the hoster did through `nid admin` reads in French.
  it('names what the recovery command line did', async () => {
    server.use(
      http.get('*/api/v1/admin/users', () => HttpResponse.json(adults)),
      http.get('*/api/v1/admin/security-journal', () => HttpResponse.json({
        items: [
          { seq: 5, at: '2026-09-26T09:00:00+00:00', event: 'admin_command', actor: { kind: 'system', personId: null, via: 'admin-cli' }, subjectPersonId: 'b', details: { command: 'grant-admin', outcome: 'ok' } },
          { seq: 4, at: '2026-09-26T08:30:00+00:00', event: 'subject_rebound', actor: { kind: 'system', personId: null, via: 'admin-cli' }, subjectPersonId: 'a', details: { had_subject: 'true' } },
        ],
        total: 3, page: 1,
      })),
    );
    const { user } = await renderApp('/admin/security', { capabilities: adminCapabilities });

    expect(await screen.findByRole('row', { name: /Commande de secours/ })).toHaveTextContent('Bob');
    expect(screen.getByRole('row', { name: /Identité rattachée/ })).toHaveTextContent('Alice');
    await user.click(screen.getByRole('combobox', { name: 'Événement' }));
    const list = await screen.findByRole('listbox');
    expect(within(list).getByRole('option', { name: 'Commande de secours' })).toBeInTheDocument();
    expect(within(list).getByRole('option', { name: 'Identité rattachée' })).toBeInTheDocument();
  });

  // #28: a withheld response says why, and which items when the admin may see them.
  it('explains why a response was withheld', async () => {
    const violation = { seq: 1, event: 'access_violation', at: '2026-09-26T07:00:00+00:00', actor: { kind: 'person', personId: 'a', via: 'ui' }, subjectPersonId: 'a' };
    server.use(
      http.get('*/api/v1/admin/users', () => HttpResponse.json(adults)),
      http.get('*/api/v1/admin/security-journal', () => HttpResponse.json({
        items: [
          { ...violation, seq: 3, details: { request: 'GetDashboardQuery', reason: 'outside_visible_set', count: '2' }, items: [{ id: 'id-shown', kind: 'loan', name: 'Prêt maison' }, { id: 'id-hidden', kind: null, name: null }] },
          { ...violation, seq: 2, details: { request: 'GetProjectionQuery', reason: 'visibility_lookup_failed', count: '1', error: 'TimeoutException' }, items: [] },
          { ...violation, seq: 1, details: { request: 'OldQuery' }, items: [] },
        ],
        total: 3, page: 1,
      })),
    );
    const { user } = await renderApp('/admin/security', { capabilities: adminCapabilities });

    const outside = await screen.findByRole('row', { name: /hors du périmètre/ });
    expect(outside).toHaveTextContent('Prêt maison (prêt), id-hidden');
    expect(outside).not.toHaveTextContent('GetDashboardQuery');
    await user.click(within(outside).getByRole('button', { name: 'Détails bruts' }));
    expect(outside).toHaveTextContent('request: GetDashboardQuery');
    expect(screen.getByRole('row', { name: /calcul des droits a échoué/ })).toBeInTheDocument();
    expect(screen.getByText('request: OldQuery')).toBeInTheDocument();
  });

  it('puts its event filter in the filter row, the label before it', async () => {
    server.use(
      http.get('*/api/v1/admin/users', () => HttpResponse.json(adults)),
      http.get('*/api/v1/admin/security-journal', () => HttpResponse.json({ items: [], total: 0, page: 1 })),
    );
    await renderApp('/admin/security', { capabilities: adminCapabilities });

    const event = await screen.findByRole('combobox', { name: 'Événement' });
    expectLabelBefore(within(screen.getByRole('group', { name: 'Filtres' })).getByText('Événement'), event);
  });

  it('filters by event', async () => {
    const asked: Array<string | null> = [];
    server.use(
      http.get('*/api/v1/admin/users', () => HttpResponse.json(adults)),
      http.get('*/api/v1/admin/security-journal', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('event'));
        return HttpResponse.json({ items: [], total: 0, page: 1 });
      }),
    );
    const { user } = await renderApp('/admin/security', { capabilities: adminCapabilities });

    await screen.findByRole('combobox', { name: 'Événement' });
    await pick(user, 'Événement', /violation d'accès/);

    await expect.poll(() => asked.at(-1)).toBe('access_violation');
  });

  it('pages the journal and drops the page when the event changes', async () => {
    const seen: string[] = [];
    const entry = { seq: 1, at: '2026-09-26T07:00:00+00:00', event: 'login_refused', actor: { kind: 'system', personId: null, via: 'job' }, subjectPersonId: null, details: {} };
    server.use(
      http.get('*/api/v1/admin/users', () => HttpResponse.json(adults)),
      http.get('*/api/v1/admin/security-journal', ({ request }) => {
        seen.push(new URL(request.url).search);
        return HttpResponse.json({ items: [entry], total: 51, page: 1 });
      }),
    );
    const { user, router } = await renderApp('/admin/security?event=login_refused', { capabilities: adminCapabilities });

    const pages = await screen.findByRole('navigation', { name: 'Pages' });
    expect(pages).toHaveTextContent('Page 1 sur 2');
    await user.click(within(pages).getByRole('button', { name: 'Suivant' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ event: 'login_refused', page: 2 }));
    await waitFor(() => expect(seen.at(-1)).toBe('?event=login_refused&page=2'));

    await pick(user, 'Événement', /violation d'accès/);
    await waitFor(() => expect(router.state.location.search).toEqual({ event: 'access_violation' }));
    await waitFor(() => expect(seen.at(-1)).toBe('?event=access_violation&page=1'));
  });
});
