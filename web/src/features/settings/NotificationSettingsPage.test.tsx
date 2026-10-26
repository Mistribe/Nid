import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

type Body = Record<string, unknown>;

const view = (over: Partial<Body> = {}) => ({
  emailEnabled: false, emailAvailable: true, hasEmail: true, action: 'immediate', info: 'digest', frequency: 'daily', weekday: 1, version: 3,
  leadDays: [
    { kind: 'rent.revision', title: 'Révision du loyer', days: 30, defaultDays: 30 },
    { kind: 'loan.end', title: 'Fin de prêt', days: 45, defaultDays: 60 },
  ],
  ...over,
});

function serve(initial: Body = view()) {
  const sent: Body[] = [];
  server.use(
    http.get('*/api/v1/platform/notifications/preferences', () => HttpResponse.json(initial)),
    http.put('*/api/v1/platform/notifications/preferences', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push(body);
      return HttpResponse.json({ ...initial, ...body, leadDays: initial.leadDays, version: (body.version as number) + 1 });
    }),
  );
  return sent;
}

beforeAll(async () => { await import('./NotificationSettingsPage'); }, 30_000);

describe('the notification settings page', () => {
  it('loads the saved choices and the nav between the settings pages', async () => {
    serve(view({ emailEnabled: true }));
    const { container } = await renderApp('/settings/notifications');

    expect(await screen.findByRole('heading', { level: 1, name: 'Notifications' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Apparence' })).toHaveAttribute('href', '/settings/appearance');
    expect(await screen.findByRole('checkbox', { name: 'Email' })).toBeChecked();
    expect(screen.getByText('Critique : toujours tout de suite, sur tous vos canaux.')).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Tout de suite' })).toBeChecked();
    expect(screen.getByRole('radio', { name: 'Chaque jour' })).toBeChecked();
    expect(screen.getByRole('heading', { level: 2, name: 'Délais' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('sends nothing until « Enregistrer », then the whole object with its version', async () => {
    const sent = serve();
    const { user } = await renderApp('/settings/notifications');

    await user.click(await screen.findByRole('checkbox', { name: 'Email' }));
    await user.click(screen.getByRole('radio', { name: 'Chaque semaine' }));
    await pick(user, 'Jour du résumé', 'jeudi');
    expect(sent).toEqual([]);

    await user.click(screen.getByRole('button', { name: /Délais/ }));
    const lead = await screen.findByRole('spinbutton', { name: 'Fin de prêt' });
    await user.clear(lead);
    await user.type(lead, '90');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{
      emailEnabled: true, action: 'immediate', info: 'digest', frequency: 'weekly', weekday: 4,
      leadDays: { 'rent.revision': 30, 'loan.end': 90 }, version: 3,
    }]));
    expect(await screen.findByText('Réglages enregistrés.')).toBeInTheDocument();
  });

  it('disables the email channel with its sentence when the host sends none', async () => {
    serve(view({ emailAvailable: false, hasEmail: true }));
    await renderApp('/settings/notifications');

    const email = await screen.findByRole('checkbox', { name: 'Email' });
    expect(email).toBeDisabled();
    expect(screen.getByText('L’hébergeur n’a pas configuré l’envoi d’emails.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Envoyer un test' })).toBeDisabled();
  });

  it('says the account has no address when the server could send', async () => {
    serve(view({ emailAvailable: false, hasEmail: false }));
    await renderApp('/settings/notifications');

    expect(await screen.findByRole('checkbox', { name: 'Email' })).toBeDisabled();
    expect(screen.getByText('Votre compte n’a pas d’adresse email.')).toBeInTheDocument();
    expect(screen.queryByText('L’hébergeur n’a pas configuré l’envoi d’emails.')).not.toBeInTheDocument();
  });

  it('asks for the weekday only for the weekly digest', async () => {
    serve();
    const { user } = await renderApp('/settings/notifications');

    await screen.findByRole('checkbox', { name: 'Email' });
    expect(screen.queryByRole('combobox', { name: 'Jour du résumé' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('radio', { name: 'Chaque semaine' }));
    expect(screen.getByRole('combobox', { name: 'Jour du résumé' })).toBeInTheDocument();
  });

  it('restores a lead time to its default', async () => {
    const sent = serve();
    const { user } = await renderApp('/settings/notifications');

    await user.click(await screen.findByRole('button', { name: /Délais/ }));
    expect(await screen.findByText('Par défaut : 60 jours')).toBeInTheDocument();
    await user.click(screen.getAllByRole('button', { name: /Rétablir/ })[1]!);
    expect(screen.getByRole('spinbutton', { name: 'Fin de prêt' })).toHaveValue(60);
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect((sent[0]!.leadDays as Body)['loan.end']).toBe(60));
  });

  it('shows the server refusal of a lead time under its field', async () => {
    serve();
    server.use(http.put('*/api/v1/platform/notifications/preferences', () => HttpResponse.json(
      { status: 422, code: 'validation', errors: [{ path: 'leadDays.loan.end', code: 'notifications.invalid_lead_days', message: 'invalid' }] }, { status: 422 })));
    const { user } = await renderApp('/settings/notifications');

    await user.click(await screen.findByRole('button', { name: /Délais/ }));
    const lead = await screen.findByRole('spinbutton', { name: 'Fin de prêt' });
    await user.clear(lead);
    await user.type(lead, '999');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByText('Entre 0 et 365 jours.')).toBeInTheDocument();
    expect(lead).toHaveAccessibleDescription(/Entre 0 et 365 jours\./);
  });

  it('offers « Recharger » when the settings changed elsewhere', async () => {
    serve();
    server.use(http.put('*/api/v1/platform/notifications/preferences', () => HttpResponse.json(
      { status: 409, code: 'notifications.preferences_conflict' }, { status: 409 })));
    const { user } = await renderApp('/settings/notifications');

    await user.click(await screen.findByRole('button', { name: 'Enregistrer' }));
    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText('Ces réglages ont changé ailleurs.')).toBeInTheDocument();

    server.use(http.get('*/api/v1/platform/notifications/preferences', () => HttpResponse.json(view({ emailEnabled: true, version: 4 }))));
    await user.click(within(alert).getByRole('button', { name: 'Recharger' }));
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Email' })).toBeChecked());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('sends a test email and says so', async () => {
    serve();
    const posted: unknown[] = [];
    server.use(http.post('*/api/v1/platform/notifications/test', async ({ request }) => { posted.push(await request.json()); return HttpResponse.json({ sent: true, error: null }); }));
    const { user } = await renderApp('/settings/notifications');

    await user.click(await screen.findByRole('button', { name: 'Envoyer un test' }));
    expect(await screen.findByText('Email envoyé.')).toBeInTheDocument();
    expect(posted).toEqual([{ channel: 'email' }]);
  });

  it('shows why a test email did not leave', async () => {
    serve();
    server.use(http.post('*/api/v1/platform/notifications/test', () => HttpResponse.json({ sent: false, error: 'SMTP refusé' })));
    const { user } = await renderApp('/settings/notifications');

    await user.click(await screen.findByRole('button', { name: 'Envoyer un test' }));
    expect(await screen.findByText(/SMTP refusé/)).toBeInTheDocument();
  });

  it('words the known error codes instead of showing them', async () => {
    serve();
    server.use(http.post('*/api/v1/platform/notifications/test', () => HttpResponse.json({ sent: false, error: 'notifications.channel_rejected' })));
    const { user } = await renderApp('/settings/notifications');

    await user.click(await screen.findByRole('button', { name: 'Envoyer un test' }));
    expect(await screen.findByText('L’email n’est pas parti : Le service a refusé le message.')).toBeInTheDocument();
    expect(screen.queryByText(/notifications.channel_rejected/)).not.toBeInTheDocument();
  });
  it('asks to wait when a test comes too soon after the last one', async () => {
    serve();
    server.use(http.post('*/api/v1/platform/notifications/test', () => HttpResponse.json({ status: 429, code: 'notifications.test_throttled', title: 'Wait a few seconds before another test.' }, { status: 429 })));
    const { user } = await renderApp('/settings/notifications');

    await user.click(await screen.findByRole('button', { name: 'Envoyer un test' }));
    expect(await screen.findByText('Patientez quelques secondes avant un nouveau test.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('saves the Discord webhook on its own and keeps the form’s version', async () => {
    const sent = serve();
    const calls: { method: string; body?: Body }[] = [];
    let discord: Body | null = null;
    server.use(
      http.get('*/api/v1/platform/notifications/preferences', () => HttpResponse.json(view({ discord }))),
      http.put('*/api/v1/platform/notifications/discord', async ({ request }) => {
        const body = (await request.json()) as Body;
        calls.push({ method: 'PUT', body });
        discord = { hint: 'wxyz', status: 'ok' };
        return HttpResponse.json(discord);
      }),
      http.post('*/api/v1/platform/notifications/test', async ({ request }) => {
        calls.push({ method: 'POST', body: (await request.json()) as Body });
        return HttpResponse.json({ sent: true, error: null });
      }),
    );
    const { user } = await renderApp('/settings/notifications');

    await user.type(await screen.findByLabelText('Webhook Discord'), 'https://discord.com/api/webhooks/1/wxyz{Enter}');
    expect(await screen.findByText(/Webhook enregistré : …wxyz/)).toBeInTheDocument();
    expect(sent).toEqual([]);
    expect(calls).toEqual([{ method: 'PUT', body: { url: 'https://discord.com/api/webhooks/1/wxyz' } }]);

    await user.click(screen.getAllByRole('button', { name: 'Envoyer un test' }).at(-1) as HTMLElement);
    await waitFor(() => expect(calls[1]).toEqual({ method: 'POST', body: { channel: 'discord_person' } }));

    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]?.version).toBe(3);
  });
  it('shows a personal webhook the test found broken, at once', async () => {
    let discord = { hint: 'wxyz', status: 'ok' };
    serve(view({ discord }));
    server.use(
      http.get('*/api/v1/platform/notifications/preferences', () => HttpResponse.json(view({ discord }))),
      http.post('*/api/v1/platform/notifications/test', () => { discord = { hint: 'wxyz', status: 'broken' }; return HttpResponse.json({ sent: false, error: 'notifications.channel_broken' }); }),
    );
    const { user } = await renderApp('/settings/notifications');

    await user.click((await screen.findAllByRole('button', { name: 'Envoyer un test' })).at(-1) as HTMLElement);
    expect(await screen.findByText(/Discord ne répond plus à ce webhook/)).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Envoyer un test' }).at(-1)).toBeDisabled();
  });
  it('shows a Discord test that fails with a server error', async () => {
    serve(view({ discord: { hint: 'wxyz', status: 'ok' } }));
    server.use(http.post('*/api/v1/platform/notifications/test', () => HttpResponse.json({ status: 500 }, { status: 500 })));
    const { user } = await renderApp('/settings/notifications');

    await user.click((await screen.findAllByRole('button', { name: 'Envoyer un test' })).at(-1) as HTMLElement);
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
