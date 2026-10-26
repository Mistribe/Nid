import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { notification } from './testData';

const PROPERTY = '11111111-1111-4111-8111-111111111111';

type Row = ReturnType<typeof notification>;

function serve(lists: { todo?: Row[]; snoozed?: Row[]; history?: Row[] }) {
  server.use(http.get('*/api/v1/platform/notifications', ({ request }) => {
    const list = new URL(request.url).searchParams.get('list') as 'todo' | 'snoozed' | 'history';
    return HttpResponse.json({ entries: lists[list] ?? [], next: null });
  }));
}

beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 9, 24, 12)); });
afterEach(() => { vi.useRealTimers(); });
beforeAll(async () => { await import('./NotificationsPage'); }, 30_000);

// A submenu item is chosen from the keyboard: jsdom pointer moves close the submenu before the click lands.
async function choose(user: ReturnType<typeof import('@testing-library/user-event').default.setup>, name: string) {
  const item = await screen.findByRole('menuitem', { name });
  item.focus();
  await user.keyboard('{Enter}');
}

const todo = () => notification(1, { level: 'action', title: 'Loyer en retard', body: 'Appartement Lyon', link: `/properties/${PROPERTY}?tab=rental`, dueOn: '2026-10-30' });

describe('the notifications page', () => {
  it('lists the open notifications with their element link', async () => {
    serve({ todo: [todo()] });
    const { container } = await renderApp('/notifications');

    expect(await screen.findByRole('heading', { level: 1, name: 'Notifications' })).toBeInTheDocument();
    expect((await screen.findAllByRole('link', { name: 'Appartement Lyon' }))[0]).toHaveAttribute('href', `/properties/${PROPERTY}?tab=rental`);
    expect(screen.getAllByText('À traiter', { selector: 'td span' }).length).toBeGreaterThan(0);
    await expectNoAxeViolations(container);
  });

  it('says when nothing waits', async () => {
    serve({});
    await renderApp('/notifications');
    expect(await screen.findByText('Rien à traiter.')).toBeInTheDocument();
  });

  it('marks everything as read', async () => {
    serve({ todo: [todo()] });
    let posted = 0;
    server.use(http.post('*/api/v1/platform/notifications/read-all', () => { posted++; return new HttpResponse(null, { status: 204 }); }));
    const { user } = await renderApp('/notifications');

    await user.click(await screen.findByRole('button', { name: 'Tout marquer comme lu' }));
    await waitFor(() => expect(posted).toBe(1));
  });

  it('snoozes for a week', async () => {
    serve({ todo: [todo()] });
    const posted: unknown[] = [];
    server.use(http.post('*/api/v1/platform/notifications/:id/snooze', async ({ request }) => { posted.push(await request.json()); return new HttpResponse(null, { status: 204 }); }));
    const { user } = await renderApp('/notifications');

    await screen.findAllByRole('link', { name: 'Appartement Lyon' });
    await user.click(screen.getAllByRole('button', { name: 'Actions' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Reporter' }));
    await choose(user, '1 semaine');

    await waitFor(() => expect(posted).toEqual([{ preset: 'week' }]));
  });

  it('snoozes until a date and shows the server refusal under the field', async () => {
    serve({ todo: [todo()] });
    const posted: unknown[] = [];
    server.use(http.post('*/api/v1/platform/notifications/:id/snooze', async ({ request }) => {
      posted.push(await request.json());
      return HttpResponse.json({ status: 422, code: 'validation', errors: [{ path: 'until', code: 'notifications.invalid_snooze', message: 'invalid' }] }, { status: 422 });
    }));
    const { user } = await renderApp('/notifications');

    await screen.findAllByRole('link', { name: 'Appartement Lyon' });
    await user.click(screen.getAllByRole('button', { name: 'Actions' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Reporter' }));
    await choose(user, 'Jusqu’au…');
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Reporter jusqu’au'), '01/01/2030');
    await user.click(within(dialog).getByRole('button', { name: 'Reporter' }));

    await waitFor(() => expect(posted).toEqual([{ until: '2030-01-01' }]));
    expect(await within(dialog).findByText('Choisissez une date dans l’année qui vient.')).toBeInTheDocument();
  });

  it('dismisses after confirmation', async () => {
    serve({ todo: [todo()] });
    let dismissed = 0;
    server.use(http.post('*/api/v1/platform/notifications/:id/dismiss', () => { dismissed++; return new HttpResponse(null, { status: 204 }); }));
    const { user } = await renderApp('/notifications');

    await screen.findAllByRole('link', { name: 'Appartement Lyon' });
    await user.click(screen.getAllByRole('button', { name: 'Actions' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Ignorer' }));
    expect(dismissed).toBe(0);
    expect(await screen.findByText('Cette notification ne reviendra pas.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(dismissed).toBe(1));
  });

  it('dismisses a snoozed row after confirmation', async () => {
    serve({ snoozed: [notification(3, { state: 'snoozed', snoozedUntil: '2026-11-15', body: 'Reporté' })] });
    const dismissed: string[] = [];
    server.use(http.post('*/api/v1/platform/notifications/:id/dismiss', ({ params }) => { dismissed.push(String(params.id)); return new HttpResponse(null, { status: 204 }); }));
    const { user } = await renderApp('/notifications?tab=snoozed');

    await screen.findAllByRole('link', { name: 'Reporté' });
    await user.click(screen.getAllByRole('button', { name: 'Actions' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Ignorer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(dismissed).toEqual([notification(3).id]));
  });

  it('reads an unread row when its link is followed, not a read one', async () => {
    serve({ todo: [todo(), notification(5, { read: true, body: 'Déjà lu', link: `/properties/${PROPERTY}` })] });
    const posted: string[] = [];
    server.use(http.post('*/api/v1/platform/notifications/:id/read', ({ params }) => { posted.push(String(params.id)); return new HttpResponse(null, { status: 204 }); }));
    const { user, router } = await renderApp('/notifications');

    await user.click((await screen.findAllByRole('link', { name: 'Déjà lu' }))[0]!);
    await waitFor(() => expect(router.state.location.pathname).toBe(`/properties/${PROPERTY}`));
    expect(posted).toEqual([]);

    await router.navigate({ to: '/notifications' });
    await user.click((await screen.findAllByRole('link', { name: 'Appartement Lyon' }))[0]!);
    await waitFor(() => expect(posted).toEqual([notification(1).id]));
  });

  it('posts nothing when the link of a resolved unread row is followed', async () => {
    serve({ history: [notification(6, { state: 'resolved', read: false, body: 'Résolu', link: `/properties/${PROPERTY}` })] });
    const posted: string[] = [];
    server.use(http.post('*/api/v1/platform/notifications/:id/read', ({ params }) => { posted.push(String(params.id)); return new HttpResponse(null, { status: 204 }); }));
    const { user, router } = await renderApp('/notifications?tab=history');

    await user.click((await screen.findAllByRole('link', { name: 'Résolu' }))[0]!);
    await waitFor(() => expect(router.state.location.pathname).toBe(`/properties/${PROPERTY}`));
    expect(posted).toEqual([]);
  });

  it('a critical row offers only Marquer comme lue', async () => {
    serve({ todo: [notification(2, { level: 'critical', critical: true, title: 'Échéance', body: 'Prêt' })] });
    const { user } = await renderApp('/notifications');

    await screen.findAllByRole('link', { name: 'Prêt' });
    expect(screen.getAllByText('Important', { selector: 'td span' }).length).toBeGreaterThan(0);
    await user.click(screen.getAllByRole('button', { name: 'Actions' })[0]!);
    expect(await screen.findByRole('menuitem', { name: 'Marquer comme lue' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Reporter' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: 'Ignorer' })).toBeNull();
  });

  it('keeps the tab in the URL, the first one absent', async () => {
    serve({ snoozed: [notification(3, { state: 'snoozed', snoozedUntil: '2026-11-15', body: 'Reporté' })], history: [notification(4, { state: 'dismissed', read: true, body: 'Ancien' })] });
    const { user, router, container } = await renderApp('/notifications');

    await screen.findByText('Rien à traiter.');
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('tab', { name: 'Reportées' }));
    expect((await screen.findAllByRole('link', { name: 'Reporté' }))[0]!).toBeInTheDocument();
    expect(screen.getAllByText('15 novembre 2026').length).toBeGreaterThan(0);
    expect(router.state.location.search).toEqual({ tab: 'snoozed' });
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('tab', { name: 'Historique' }));
    expect((await screen.findAllByRole('link', { name: 'Ancien' }))[0]!).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Actions' })).toBeNull();
    expect(router.state.location.search).toEqual({ tab: 'history' });
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('tab', { name: 'À traiter' }));
    await waitFor(() => expect(router.state.location.search).toEqual({}));
  });
});

describe('paging', () => {
  const row = (n: number) => notification(n, { body: `Ligne ${n}` });

  it('loads the next page on « Voir plus » and appends it', async () => {
    const befores: (string | null)[] = [];
    server.use(http.get('*/api/v1/platform/notifications', ({ request }) => {
      const before = new URL(request.url).searchParams.get('before');
      befores.push(before);
      return HttpResponse.json(before ? { entries: [row(2)], next: null } : { entries: [row(1)], next: row(1).id });
    }));
    const { user } = await renderApp('/notifications');

    await user.click(await screen.findByRole('button', { name: 'Voir plus' }));
    expect((await screen.findAllByRole('link', { name: 'Ligne 2' })).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('link', { name: 'Ligne 1' }).length).toBeGreaterThan(0);
    expect(befores).toEqual([null, row(1).id]);
    expect(screen.queryByRole('button', { name: 'Voir plus' })).toBeNull();
  });

  it('shows no button without a cursor', async () => {
    serve({ todo: [row(1)] });
    await renderApp('/notifications');
    await screen.findAllByRole('link', { name: 'Ligne 1' });
    expect(screen.queryByRole('button', { name: 'Voir plus' })).toBeNull();
  });

  it('an empty follow-up page ends the list', async () => {
    server.use(http.get('*/api/v1/platform/notifications', ({ request }) => {
      const before = new URL(request.url).searchParams.get('before');
      return HttpResponse.json(before ? { entries: [], next: row(1).id } : { entries: [row(1)], next: row(1).id });
    }));
    const { user } = await renderApp('/notifications');

    await user.click(await screen.findByRole('button', { name: 'Voir plus' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Voir plus' })).toBeNull());
  });
});
