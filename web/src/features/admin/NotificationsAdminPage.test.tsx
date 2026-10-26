import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities, allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { formatInstant } from './format';

const configured = { smtpConfigured: true, smtpHost: 'smtp.example.org', failures: [] };
const failure = { id: '11111111-1111-1111-1111-111111111111', channel: 'email', kind: 'rent.due', at: '2026-10-20T08:00:00+00:00', error: 'notifications.channel_unreachable 421' };
const digest = { id: '22222222-2222-2222-2222-222222222222', channel: 'email', kind: 'digest', at: '2026-10-21T08:00:00+00:00', error: 'notifications.channel_rejected 550' };

const serve = (view: object) => server.use(http.get('*/api/v1/admin/notifications', () => HttpResponse.json(view)));

describe('Administration › Notifications', () => {
  it('sends a non-admin to the forbidden page', async () => {
    await renderApp('/admin/notifications', { capabilities: allCapabilities });
    expect(await screen.findByRole('heading', { name: "Vous n'avez pas le droit de faire cette action." })).toBeInTheDocument();
  });

  it('is a section of the admin navigation, marked current', async () => {
    serve(configured);
    await renderApp('/admin/notifications', { capabilities: adminCapabilities });
    const sections = screen.getByRole('navigation', { name: "Sections de l'administration" });
    expect(within(sections).getByRole('link', { name: 'Notifications' })).toHaveAttribute('aria-current', 'page');
  });

  it('shows the configured host and the empty failures', async () => {
    serve(configured);
    const { container } = await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    expect(await screen.findByText('Configuré : smtp.example.org')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: "M'envoyer un email de test" })).toBeEnabled();
    expect(screen.getByText('Aucun envoi en échec ces 30 derniers jours.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('names the variables to set when SMTP is not configured, and disables the test', async () => {
    serve({ smtpConfigured: false, smtpHost: null, failures: [] });
    const { container } = await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    expect(await screen.findByText('Non configuré')).toBeInTheDocument();
    for (const name of ['Host', 'Port', 'From', 'User', 'Password', 'Security'])
      expect(screen.getByText(`Nid__Smtp__${name}`).tagName).toBe('CODE');
    expect(screen.getByRole('button', { name: "M'envoyer un email de test" })).toBeDisabled();
    await expectNoAxeViolations(container);
  });

  it('posts the test and shows that it was sent', async () => {
    serve(configured);
    let body: unknown;
    server.use(http.post('*/api/v1/admin/notifications/test', async ({ request }) => {
      body = await request.json();
      return HttpResponse.json({ sent: true, error: null });
    }));
    await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    await userEvent.click(await screen.findByRole('button', { name: "M'envoyer un email de test" }));

    expect(await screen.findByRole('status')).toHaveTextContent('Email de test envoyé.');
    expect(body).toEqual({ channel: 'email' });
  });

  it('shows the error of a test that was not sent', async () => {
    serve(configured);
    server.use(http.post('*/api/v1/admin/notifications/test', () => HttpResponse.json({ sent: false, error: 'notifications.channel_unreachable 421' })));
    await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    await userEvent.click(await screen.findByRole('button', { name: "M'envoyer un email de test" }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Email de test non envoyé');
    expect(screen.getByRole('alert')).toHaveTextContent('Le serveur d’envoi ne répond pas.');
    expect(screen.getByRole('alert')).toHaveTextContent('notifications.channel_unreachable 421');
  });

  it('lists the failed deliveries: channel, kind, queued time and error', async () => {
    serve({ ...configured, failures: [failure, digest] });
    const { container } = await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    await screen.findByText('Configuré : smtp.example.org');
    const table = screen.getByRole('table', { name: 'Envois en échec' });
    for (const header of ['Canal', 'Type', 'Mise en file', 'Erreur'])
      expect(within(table).getByRole('columnheader', { name: header })).toBeInTheDocument();
    const rent = within(table).getByText('rent.due');
    expect(rent.tagName).toBe('CODE');
    expect(rent.closest('tr')).toHaveTextContent(formatInstant(failure.at));
    expect(rent.closest('tr')).toHaveTextContent('notifications.channel_unreachable 421');
    expect(rent.closest('tr')).toHaveTextContent('Le serveur d’envoi ne répond pas.');
    expect(within(table).getByText('Résumé').closest('tr')).toHaveTextContent('Le serveur d’envoi a refusé l’email.');
    expect(within(table).getByText('Résumé').closest('tr')).toHaveTextContent('notifications.channel_rejected 550');
    expect(screen.queryByText('Aucun envoi en échec ces 30 derniers jours.')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('lists two failures of the same kind and instant as two rows', async () => {
    const twin = { ...failure, id: '33333333-3333-3333-3333-333333333333' };
    serve({ ...configured, failures: [failure, twin] });
    await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    await screen.findByText('Configuré : smtp.example.org');
    const table = screen.getByRole('table', { name: 'Envois en échec' });
    expect(within(table).getAllByText('rent.due')).toHaveLength(2);
  });
});
