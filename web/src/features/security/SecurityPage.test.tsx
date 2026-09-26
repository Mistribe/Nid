import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { formatInstant } from '@/features/admin/format';
import { expectNoAxeViolations } from '@/test/axe';
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
  it('lists my latest security events in French', async () => {
    server.use(http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json(events)));
    const { container } = await renderApp('/security');

    const login = await screen.findByRole('row', { name: /Connexion/ });
    expect(login).toHaveTextContent(formatInstant('2026-09-26T08:00:00+00:00'));
    expect(login).toHaveTextContent('Navigateur');
    const granted = screen.getByRole('row', { name: /Rôle admin accordé/ });
    expect(granted).toBeInTheDocument();
    expect(granted).toHaveTextContent('Ligne de commande'); // 1F4: the recovery CLI's origin, named in French
    expect(screen.getByText(/ne ferme pas votre session auprès du fournisseur d'identité/)).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  // A reload would go through login, which the identity provider's own session answers at once: the adult would be
  // signed straight back in. Like signing out, it lands on the signed-out page (1F3a spec §7, security.spec.ts).
  it('signs me out everywhere after a confirmation, then lands on the signed-out page', async () => {
    let revoked = false;
    server.use(
      http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({ entries: [], next: null })),
      http.post('*/api/v1/core/me/revoke-sessions', () => { revoked = true; return new HttpResponse(null, { status: 204 }); }),
    );
    const { user, location } = await renderApp('/security');

    await user.click(await screen.findByRole('button', { name: 'Déconnecter partout' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await expect.poll(() => location.assign.mock.calls.length).toBe(1);
    expect(location.assign).toHaveBeenCalledWith('/signed-out');
    expect(location.reload).not.toHaveBeenCalled();
    expect(revoked).toBe(true);
  });
});
