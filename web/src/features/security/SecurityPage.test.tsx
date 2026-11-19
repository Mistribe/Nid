import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { formatInstant } from '@/lib/format';
import { expectNoAxeViolations } from '@/test/axe';
import { expectOnePrimary } from '@/test/headerActions';
import { aliceSession } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const events = {
  entries: [
    { seq: 2, at: '2026-09-26T08:00:00+00:00', event: 'login', actor: { kind: 'person', personId: 'a', via: 'ui' }, subjectPersonId: 'a', details: {} },
    { seq: 1, at: '2026-09-25T08:00:00+00:00', event: 'admin_granted', actor: { kind: 'person', personId: 'b', via: 'admin-cli' }, subjectPersonId: 'a', details: {} },
  ],
  next: null,
};

describe('Sécurité', () => {
  it('says where my account stands under the title, then opens on the Connexion tab', async () => {
    server.use(http.get('*/api/v1/core/me/tokens', () => HttpResponse.json([
      { id: 't1', name: 'a', preset: 'read', prefix: 'p', createdAt: '2026-10-01T00:00:00Z', expiresAt: '2027-10-01T00:00:00Z', lastUsedAt: null, revokedAt: null, status: 'active' },
      { id: 't2', name: 'b', preset: 'read', prefix: 'q', createdAt: '2026-10-01T00:00:00Z', expiresAt: '2027-10-01T00:00:00Z', lastUsedAt: null, revokedAt: null, status: 'revoked' },
    ])));
    const { container } = await renderApp('/security');

    expect(await screen.findByRole('heading', { name: 'Sécurité', level: 1 })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Connexion', selected: true })).toBeInTheDocument();
    const status = container.querySelector('[data-stat-line]') as HTMLElement;
    expect(status).toHaveTextContent('Fournisseur d’identité');
    await waitFor(() => expect(status).toHaveTextContent('Jetons actifs1'));
    expect(screen.getByRole('region', { name: 'Mes sessions' })).toBeInTheDocument();
    expectOnePrimary(container);
    await expectNoAxeViolations(container);
  });

  it('shows the token tab with its single primary', async () => {
    const { container } = await renderApp('/security?tab=tokens');

    const tokens = await screen.findByRole('region', { name: 'Mes jetons' });
    expect(within(tokens.querySelector('[data-section-actions]') as HTMLElement).getByRole('button', { name: 'Créer un jeton' })).toBeInTheDocument();
    expectOnePrimary(container);
  });

  it('lists my latest events in French, without the assistant’s reads by default', async () => {
    const asked: (string | null)[] = [];
    server.use(http.get('*/api/v1/platform/audit/me/security', ({ request }) => { asked.push(new URL(request.url).searchParams.get('kind')); return HttpResponse.json(events); }));
    const { container } = await renderApp('/security?tab=activity');

    const login = await screen.findByRole('row', { name: /Connexion/ });
    expect(login).toHaveTextContent(formatInstant('2026-09-26T08:00:00+00:00'));
    expect(login).toHaveTextContent('Navigateur');
    expect(screen.getByRole('row', { name: /Rôle admin accordé/ })).toHaveTextContent('Ligne de commande'); // 1F4: the recovery CLI's origin, named in French
    expect(asked).toEqual(['access']);
    await expectNoAxeViolations(container);
  });

  it('filters the events on the server and keeps the choice in the URL', async () => {
    const asked: (string | null)[] = [];
    server.use(http.get('*/api/v1/platform/audit/me/security', ({ request }) => { asked.push(new URL(request.url).searchParams.get('kind')); return HttpResponse.json({ entries: [], next: null }); }));
    const { user, router } = await renderApp('/security?tab=activity');

    await user.click(await screen.findByRole('radio', { name: 'Assistant' }));

    await waitFor(() => expect(asked).toEqual(['access', 'assistant']));
    expect(router.state.location.search).toEqual({ tab: 'activity', show: 'assistant' });
  });

  it('marks refusals, shows what each event carries, and loads older events with the cursor', async () => {
    const befores: (string | null)[] = [];
    server.use(http.get('*/api/v1/platform/audit/me/security', ({ request }) => {
      const before = new URL(request.url).searchParams.get('before');
      befores.push(before);
      return HttpResponse.json(before ? { entries: [events.entries[1]], next: null } : {
        entries: [
          { seq: 9, at: '2026-09-27T08:00:00+00:00', event: 'token_auth_failed', actor: { kind: 'person', personId: 'a', via: 'api' }, subjectPersonId: 'a', details: { prefix: 'abcd1234' } },
          { seq: 8, at: '2026-09-27T07:00:00+00:00', event: 'token_created', actor: { kind: 'person', personId: 'a', via: 'ui' }, subjectPersonId: 'a', details: { preset: 'propose' } },
        ],
        next: 8,
      });
    }));
    const { user } = await renderApp('/security?tab=activity');

    const refused = await screen.findByRole('row', { name: /Jeton refusé/ });
    expect(within(refused).getByText('Jeton refusé').closest('span')).toHaveClass('text-status-warning-text');
    expect(refused).toHaveTextContent('Jeton abcd1234…');
    expect(screen.getByRole('row', { name: /Jeton créé/ })).toHaveTextContent('Droits : Lecture + propositions');
    await user.click(screen.getByRole('button', { name: 'Afficher plus' }));

    expect(await screen.findByRole('row', { name: /Rôle admin accordé/ })).toBeInTheDocument();
    expect(befores).toEqual([null, '8']);
    expect(screen.queryByRole('button', { name: 'Afficher plus' })).toBeNull();
  });

  // A reload would go through login, which the identity provider's own session answers at once: the adult would be
  // signed straight back in. Like signing out, it lands on the signed-out page (1F3a spec §7, security.spec.ts).
  it('signs me out everywhere after a confirmation that reminds the identity provider, then lands on the signed-out page', async () => {
    let body: unknown;
    server.use(http.post('*/api/v1/core/me/revoke-sessions', async ({ request }) => { body = await request.json(); return new HttpResponse(null, { status: 204 }); }));
    const { user, location } = await renderApp('/security');

    await user.click(await screen.findByRole('button', { name: 'Déconnecter partout' }));
    const confirm = await screen.findByRole('alertdialog');
    expect(confirm).toHaveTextContent(/ne ferme pas votre session auprès du fournisseur d'identité/);
    expect(confirm).not.toHaveTextContent('jetons d’accès cesseront');
    await user.click(within(confirm).getByRole('button', { name: 'Confirmer' }));

    await expect.poll(() => location.assign.mock.calls.length).toBe(1);
    expect(location.assign).toHaveBeenCalledWith('/signed-out');
    expect(location.reload).not.toHaveBeenCalled();
    expect(body).toEqual({ revokeTokens: false });
  });

  it('also revokes my tokens when I tick the box in the confirmation, which then says so', async () => {
    let body: unknown;
    server.use(http.post('*/api/v1/core/me/revoke-sessions', async ({ request }) => { body = await request.json(); return new HttpResponse(null, { status: 204 }); }));
    const { user, location } = await renderApp('/security');

    await user.click(await screen.findByRole('button', { name: 'Déconnecter partout' }));
    const confirm = await screen.findByRole('alertdialog');
    await user.click(within(confirm).getByRole('checkbox', { name: 'Révoquer aussi mes jetons d’accès' }));
    expect(confirm).toHaveTextContent('vos jetons d’accès cesseront de fonctionner');
    await user.click(within(confirm).getByRole('button', { name: 'Confirmer' }));

    await expect.poll(() => location.assign.mock.calls.length).toBe(1);
    expect(body).toEqual({ revokeTokens: true });
  });
});

describe('Sécurité, compte local (issue 215)', () => {
  const local = { ...aliceSession, methods: ['password'] };

  it('changes my password with the current one, then reloads to take the reissued session', async () => {
    let sent: unknown = null;
    server.use(
      http.get('*/api/v1/core/me/credential', () => HttpResponse.json({ login: 'alice', passwordChangedAt: '2026-11-01T08:00:00+00:00', totpEnabled: false, recoveryCodesLeft: 0 })),
      http.put('*/api/v1/core/me/password', async ({ request }) => { sent = await request.json(); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user, location } = await renderApp('/security', { session: local });

    expect(await screen.findByText(/Identifiant alice · changé le/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Changer' }));
    const dialog = await screen.findByRole('dialog', { name: 'Changer le mot de passe' });
    await user.type(within(dialog).getByLabelText('Mot de passe actuel'), 'a long walk to the lighthouse');
    await user.type(within(dialog).getByLabelText('Nouveau mot de passe'), 'another long walk to the sea');
    await user.click(within(dialog).getByRole('button', { name: 'Changer le mot de passe' }));

    await waitFor(() => expect(location.reload).toHaveBeenCalled());
    expect(sent).toEqual({ current: 'a long walk to the lighthouse', new: 'another long walk to the sea' });
  });

  it('says the current password is wrong', async () => {
    server.use(
      http.get('*/api/v1/core/me/credential', () => HttpResponse.json({ login: 'alice', passwordChangedAt: '2026-11-01T08:00:00+00:00', totpEnabled: false, recoveryCodesLeft: 0 })),
      http.put('*/api/v1/core/me/password', () => HttpResponse.json({ status: 400, code: 'auth.current_password_wrong' }, { status: 400 })),
    );
    const { user } = await renderApp('/security', { session: local });

    await user.click(await screen.findByRole('button', { name: 'Changer' }));
    await user.type(await screen.findByLabelText('Mot de passe actuel'), 'wrong one entirely');
    await user.type(screen.getByLabelText('Nouveau mot de passe'), 'another long walk to the sea');
    await user.click(screen.getByRole('button', { name: 'Changer le mot de passe' }));

    expect(await screen.findByText(/mot de passe actuel est incorrect/)).toBeInTheDocument();
  });

  it('turns two-step verification on with the key and a first code, then shows the recovery codes once', async () => {
    let confirmed: unknown = null;
    let on = false;
    const codes = Array.from({ length: 10 }, (_, i) => `ABCD-EFGH-JKMN-PQ${String(i).padStart(2, '0')}`);
    server.use(
      http.get('*/api/v1/core/me/credential', () => HttpResponse.json({
        login: 'alice', passwordChangedAt: '2026-11-01T08:00:00+00:00', totpEnabled: on, recoveryCodesLeft: on ? 10 : 0,
      })),
      http.post('*/api/v1/core/me/totp', () => HttpResponse.json({
        secret: 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP', otpauthUri: 'otpauth://totp/Nid:alice?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Nid',
      })),
      http.post('*/api/v1/core/me/totp/confirm', async ({ request }) => {
        confirmed = await request.json();
        on = true;
        return HttpResponse.json({ codes });
      }),
    );
    const { user, container } = await renderApp('/security', { session: local });

    const row = await screen.findByRole('region', { name: 'Validation en deux étapes' });
    await waitFor(() => expect(within(row).getByText('Désactivée')).toHaveClass('text-status-warning-text'));
    expect(row).toHaveTextContent(/Votre mot de passe suffit/);
    expectOnePrimary(container);
    await user.click(within(row).getByRole('button', { name: 'Activer' }));
    const setup = await screen.findByRole('dialog', { name: 'Activer la validation en deux étapes' });
    expect(within(setup).getByTestId('totp-secret')).toHaveTextContent('JBSW Y3DP EHPK 3PXP');
    // Drawn in the browser from the otpauth address: the secret never goes to a third party.
    expect(await within(setup).findByRole('img', { name: /QR code à scanner/ })).toBeInTheDocument();
    expect(within(setup).getByRole('link', { name: "Ouvrir dans l'application" })).toHaveAttribute('href', expect.stringMatching(/^otpauth:\/\/totp\//));
    await user.type(within(setup).getByLabelText('Code'), '123456');
    await user.click(within(setup).getByRole('button', { name: 'Activer' }));

    const shown = await screen.findByRole('dialog', { name: 'Vos codes de secours' });
    expect(within(within(shown).getByRole('list')).getAllByRole('listitem')).toHaveLength(10);
    expect(confirmed).toEqual({ code: '123456' });
    await user.click(within(shown).getByRole('button', { name: "J'ai noté mes codes" }));

    expect(await screen.findByText('Validation en deux étapes activée.')).toBeInTheDocument();
    expect(await screen.findByText('Codes de secours restants : 10.')).toBeInTheDocument();
    expect(screen.queryByText('ABCD-EFGH-JKMN-PQ00')).not.toBeInTheDocument();
  });

  it('turns two-step verification off with the password and a code, and warns when few recovery codes are left', async () => {
    let sent: unknown = null;
    server.use(
      http.get('*/api/v1/core/me/credential', () => HttpResponse.json({
        login: 'alice', passwordChangedAt: '2026-11-01T08:00:00+00:00', totpEnabled: sent === null, recoveryCodesLeft: 2,
      })),
      http.delete('*/api/v1/core/me/totp', async ({ request }) => { sent = await request.json(); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp('/security', { session: local });

    expect(await screen.findByText(/ne vous reste que 2 codes de secours/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Autres actions de la validation en deux étapes' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Désactiver' }));
    const dialog = await screen.findByRole('dialog', { name: 'Désactiver la validation en deux étapes' });
    await user.type(within(dialog).getByLabelText('Mot de passe'), 'a long walk to the lighthouse');
    await user.type(within(dialog).getByLabelText("Code de l'application ou de secours"), '654321');
    await user.click(within(dialog).getByRole('button', { name: 'Désactiver' }));

    expect(await screen.findByText('Validation en deux étapes désactivée.')).toBeInTheDocument();
    expect(sent).toEqual({ password: 'a long walk to the lighthouse', code: '654321' });
  });

  it('shows no password section to an adult of the identity provider', async () => {
    await renderApp('/security');
    expect(await screen.findByRole('heading', { name: 'Sécurité', level: 1 })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Mot de passe' })).not.toBeInTheDocument();
  });
});
