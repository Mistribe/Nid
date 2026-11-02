import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it, vi } from 'vitest';
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

  it('asks to wait when a test comes too soon after the last one', async () => {
    serve(configured);
    server.use(http.post('*/api/v1/admin/notifications/test', () => HttpResponse.json({ status: 429, code: 'notifications.test_throttled', title: 'Wait a few seconds before another test.' }, { status: 429 })));
    await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    await userEvent.click(await screen.findByRole('button', { name: "M'envoyer un email de test" }));

    expect(await screen.findByRole('status')).toHaveTextContent('Patientez quelques secondes avant un nouveau test.');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
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
    expect(within(table).getByText('Résumé').closest('tr')).toHaveTextContent('Le service a refusé le message.');
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
  it('sets, tests and removes the household Discord webhook', async () => {
    serve({ ...configured, discord: { hint: 'wxyz', status: 'ok' } });
    const calls: string[] = [];
    server.use(
      http.put('*/api/v1/admin/notifications/discord', async ({ request }) => { calls.push(`PUT ${JSON.stringify(await request.json())}`); return HttpResponse.json({ hint: 'abcd', status: 'ok' }); }),
      http.delete('*/api/v1/admin/notifications/discord', () => { calls.push('DELETE'); return new HttpResponse(null, { status: 204 }); }),
      http.post('*/api/v1/admin/notifications/test', async ({ request }) => { calls.push(`POST ${JSON.stringify(await request.json())}`); return HttpResponse.json({ sent: true, error: null }); }),
    );
    const user = userEvent.setup();
    await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    expect(await screen.findByRole('heading', { name: 'Discord du foyer' })).toBeInTheDocument();
    expect(screen.getByText('Ne reçoit que ce que tous les adultes peuvent lire, sans nom ni montant.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Envoyer un test' }));
    await user.click(screen.getByRole('button', { name: 'Remplacer' }));
    await user.type(screen.getByLabelText('Webhook Discord'), 'https://discord.com/api/webhooks/1/abcd');
    await user.click(screen.getByRole('button', { name: 'Enregistrer le webhook' }));
    await user.click(await screen.findByRole('button', { name: 'Retirer' }));
    expect(await screen.findByText('Le foyer ne recevra plus rien sur Discord.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));
    await vi.waitFor(() => expect(calls).toEqual([
      'POST {"channel":"discord_household"}', 'PUT {"url":"https://discord.com/api/webhooks/1/abcd"}', 'DELETE',
    ]));
  });
  it('shows a household webhook the test found broken, and disables the test', async () => {
    let discord = { hint: 'wxyz', status: 'ok' };
    serve({ ...configured, discord });
    server.use(
      http.get('*/api/v1/admin/notifications', () => HttpResponse.json({ ...configured, discord })),
      http.post('*/api/v1/admin/notifications/test', () => { discord = { hint: 'wxyz', status: 'broken' }; return HttpResponse.json({ sent: false, error: 'notifications.channel_broken' }); }),
    );
    await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    await userEvent.click(await screen.findByRole('button', { name: 'Envoyer un test' }));
    expect(await screen.findByText(/Discord ne répond plus à ce webhook/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Envoyer un test' })).toBeDisabled();
  });
  it('asks to wait when a household Discord test comes too soon after the last one', async () => {
    serve({ ...configured, discord: { hint: 'wxyz', status: 'ok' } });
    server.use(http.post('*/api/v1/admin/notifications/test', () => HttpResponse.json({ status: 429, code: 'notifications.test_throttled', title: 'Wait a few seconds before another test.' }, { status: 429 })));
    await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    await userEvent.click(await screen.findByRole('button', { name: 'Envoyer un test' }));
    expect(await screen.findByText('Patientez quelques secondes avant un nouveau test.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
  it('shows a household Discord test that fails with a server error, without a dangling colon', async () => {
    serve({ ...configured, discord: { hint: 'wxyz', status: 'ok' } });
    server.use(http.post('*/api/v1/admin/notifications/test', () => HttpResponse.json({ status: 500 }, { status: 500 })));
    await renderApp('/admin/notifications', { capabilities: adminCapabilities });

    await userEvent.click(await screen.findByRole('button', { name: 'Envoyer un test' }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByText(/n’est pas parti/)).not.toBeInTheDocument();
  });
});
