import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

describe('the critical banner', () => {
  it('says an important notification waits, with a link to the centre', async () => {
    server.use(http.get('*/api/v1/platform/notifications/count', () => HttpResponse.json({ unread: 1, criticalUnread: 1 })));
    await renderApp('/subscriptions');
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Une notification importante attend votre lecture.');
    expect(screen.getByRole('link', { name: 'Voir' })).toHaveAttribute('href', '/notifications');
  });

  it('shows nothing without a critical notification', async () => {
    await renderApp('/subscriptions');
    await screen.findByRole('button', { name: 'Notifications' });
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
