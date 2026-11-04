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

  // « Jamais sur le fond nu »: the warning tint sits on a frosted surface, not on the halo.
  it('tints a material surface, never the bare ground', async () => {
    server.use(http.get('*/api/v1/platform/notifications/count', () => HttpResponse.json({ unread: 1, criticalUnread: 1 })));
    await renderApp('/subscriptions');
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveClass('material', 'rounded-pane');
    expect(alert).not.toHaveClass('bg-status-warning/10');
    expect(alert.firstElementChild).toHaveClass('bg-status-warning/10');
  });

  // The status is not carried by the tint's colour alone.
  it('leads with a warning icon', async () => {
    server.use(http.get('*/api/v1/platform/notifications/count', () => HttpResponse.json({ unread: 1, criticalUnread: 1 })));
    await renderApp('/subscriptions');
    const icon = (await screen.findByRole('alert')).querySelector('svg.lucide-triangle-alert');
    expect(icon).toHaveAttribute('aria-hidden', 'true');
    expect(icon).toHaveClass('text-status-warning-text');
  });

  it('shows nothing without a critical notification', async () => {
    await renderApp('/subscriptions');
    await screen.findByRole('button', { name: 'Notifications' });
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
