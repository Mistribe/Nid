import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { notification } from '@/features/notifications/testData';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const count = (unread: number, criticalUnread = 0) => http.get('*/api/v1/platform/notifications/count', () => HttpResponse.json({ unread, criticalUnread }));

describe('the notification bell', () => {
  it('shows the unread count in the label and the badge', async () => {
    server.use(count(3));
    await renderApp('/subscriptions');
    const bell = await screen.findByRole('button', { name: 'Notifications, 3 non lues' });
    expect(bell).toHaveTextContent('3');
  });

  it('turns to the warning colour while an important one is unread, and says so in its name, with no banner', async () => {
    server.use(count(3, 1));
    const { container } = await renderApp('/subscriptions');
    const bell = await screen.findByRole('button', { name: 'Notifications, 3 non lues, dont 1 importante' });
    expect(bell).toHaveClass('bg-status-warning/15');
    expect(bell.querySelector('.bg-status-warning')).toHaveTextContent('3');
    expect(bell.querySelector('.bg-primary')).toBeNull();
    // The bell carries it: no banner under the top bar any more.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(container).not.toHaveTextContent('Une notification importante');
  });

  it('stays in the primary colour without an important one unread', async () => {
    server.use(count(2));
    await renderApp('/subscriptions');
    const bell = await screen.findByRole('button', { name: 'Notifications, 2 non lues' });
    expect(bell).not.toHaveClass('bg-status-warning/15');
    expect(bell.querySelector('.bg-primary')).toHaveTextContent('2');
  });

  it('lists the unread important ones first, even older than the latest, marked in words', async () => {
    const asked: string[] = [];
    const older = notification(20, { level: 'critical', critical: true, title: 'Échéance' });
    server.use(
      count(10, 1),
      http.get('*/api/v1/platform/notifications', ({ request }) => {
        const list = new URL(request.url).searchParams.get('list');
        asked.push(list ?? '');
        // The eight latest to do hold no important one, and a read important one is not singled out.
        return HttpResponse.json(list === 'critical'
          ? { entries: [older], next: null }
          : { entries: [notification(1, { level: 'critical', critical: true, read: true, title: 'Ancienne' }), ...Array.from({ length: 7 }, (_, i) => notification(i + 2))], next: null });
      }),
    );
    const { user } = await renderApp('/subscriptions');
    await user.click(await screen.findByRole('button', { name: 'Notifications, 10 non lues, dont 1 importante', expanded: false }));
    const popover = await screen.findByRole('dialog');
    await within(popover).findByText('Échéance');
    const links = within(popover).getAllByRole('link').filter((l) => /Titre|Échéance|Ancienne/.test(l.textContent ?? ''));
    expect(links).toHaveLength(8);
    expect(links[0]).toHaveTextContent(/^ImportanteÉchéance/);
    expect(links[1]).toHaveTextContent(/^Ancienne/);
    expect(links[1]).not.toHaveTextContent('Importante');
    expect(asked.sort()).toEqual(['critical', 'todo']);
    await expectNoAxeViolations(popover);
  });

  it('asks for the important ones only while one is unread', async () => {
    const asked: string[] = [];
    server.use(
      count(1),
      http.get('*/api/v1/platform/notifications', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('list') ?? '');
        return HttpResponse.json({ entries: [notification(1)], next: null });
      }),
    );
    const { user } = await renderApp('/subscriptions');
    await user.click(await screen.findByRole('button', { name: 'Notifications, 1 non lue', expanded: false }));
    await within(await screen.findByRole('dialog')).findByText('Titre 1');
    expect(asked).toEqual(['todo']);
  });

  it('has no badge at zero', async () => {
    await renderApp('/subscriptions');
    const bell = await screen.findByRole('button', { name: 'Notifications' });
    expect(bell).toHaveTextContent('');
  });

  it('opens the eight latest and marks all as read', async () => {
    let unread = 10;
    let posted = false;
    server.use(
      http.get('*/api/v1/platform/notifications/count', () => HttpResponse.json({ unread, criticalUnread: 0 })),
      http.get('*/api/v1/platform/notifications', () => HttpResponse.json({ entries: Array.from({ length: 10 }, (_, i) => notification(i + 1)), next: null })),
      http.post('*/api/v1/platform/notifications/read-all', () => { posted = true; unread = 0; return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp('/subscriptions');
    await user.click(await screen.findByRole('button', { name: 'Notifications, 10 non lues', expanded: false }));
    const popover = await screen.findByRole('dialog');
    await within(popover).findByText('Titre 1');
    expect(within(popover).getAllByRole('link').filter((l) => /Titre/.test(l.textContent ?? ''))).toHaveLength(8);
    expect(within(popover).getByRole('link', { name: 'Tout voir' })).toHaveAttribute('href', '/notifications');
    await expectNoAxeViolations(popover);
    await user.click(within(popover).getByRole('button', { name: 'Tout marquer comme lu' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Notifications' })).toBeInTheDocument());
    expect(posted).toBe(true);
  });

  it('says one unread in the singular', async () => {
    server.use(count(1));
    await renderApp('/subscriptions');
    expect(await screen.findByRole('button', { name: 'Notifications, 1 non lue' })).toBeInTheDocument();
  });

  it('follows an entry link with its search and closes the popover', async () => {
    server.use(
      count(1),
      http.get('*/api/v1/platform/notifications', () => HttpResponse.json({ entries: [notification(1, { link: '/subscriptions?tab=cost' })], next: null })),
    );
    const { user, router } = await renderApp('/routine');
    await user.click(await screen.findByRole('button', { name: 'Notifications, 1 non lue', expanded: false }));
    await user.click(await screen.findByRole('link', { name: /Titre 1/ }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/subscriptions'));
    expect(router.state.location.search).toMatchObject({ tab: 'cost' });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('reads an unread entry when its link is followed, and posts nothing for a read one', async () => {
    const posted: string[] = [];
    server.use(
      count(1),
      http.get('*/api/v1/platform/notifications', () => HttpResponse.json({
        entries: [notification(1, { link: '/subscriptions' }), notification(2, { link: '/subscriptions', read: true })], next: null,
      })),
      http.post('*/api/v1/platform/notifications/:id/read', ({ params }) => { posted.push(String(params.id)); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp('/routine');
    await user.click(await screen.findByRole('button', { name: 'Notifications, 1 non lue', expanded: false }));

    await user.click(await screen.findByRole('link', { name: /Titre 2/ }));
    expect(posted).toEqual([]);

    await user.click(await screen.findByRole('button', { name: /Notifications/, expanded: false }));
    await user.click(await screen.findByRole('link', { name: /Titre 1/ }));
    await waitFor(() => expect(posted).toEqual([notification(1).id]));
  });
});
