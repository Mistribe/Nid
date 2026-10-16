import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const token = (over: Record<string, unknown> = {}) => ({
  id: 't1', name: 'Claude sur mon laptop', preset: 'propose', prefix: 'abcd1234', createdAt: '2026-10-01T08:00:00+00:00',
  expiresAt: '2027-01-01T08:00:00+00:00', lastUsedAt: null, revokedAt: null, status: 'active', ...over,
});
const SECRET = 'nid_pat_abcd1234_0123456789012345678901234567890123456789012';

describe('Mes jetons', () => {
  it('lists my tokens with their rights, expiry and state', async () => {
    server.use(
      http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({ entries: [], next: null })),
      http.get('*/api/v1/core/me/tokens', () => HttpResponse.json([token(), token({ id: 't2', name: 'Ancien', status: 'revoked', revokedAt: '2026-10-02T08:00:00+00:00' })])),
    );
    const { container } = await renderApp('/security');

    const row = await screen.findByRole('row', { name: /Claude sur mon laptop/ });
    expect(row).toHaveTextContent('Lecture + propositions');
    expect(row).toHaveTextContent('Actif');
    expect(row).toHaveTextContent('Jamais');
    expect(within(screen.getByRole('row', { name: /Ancien/ })).queryByRole('button', { name: 'Révoquer' })).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('creates a token with « Lecture + propositions » by default and shows its secret once', async () => {
    let sent: unknown;
    server.use(
      http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({ entries: [], next: null })),
      http.post('*/api/v1/core/me/tokens', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json({ token: token(), secret: SECRET }, { status: 201 });
      }),
    );
    const { user, container } = await renderApp('/security');

    await user.click(await screen.findByRole('button', { name: 'Créer un jeton' }));
    const dialog = screen.getByRole('dialog', { name: 'Créer un jeton' });
    expect(within(dialog).getByRole('radio', { name: /Lecture \+ propositions/ })).toBeChecked();
    await user.type(within(dialog).getByLabelText('Nom'), 'Claude sur mon laptop');
    await user.click(within(dialog).getByRole('button', { name: 'Créer' }));

    const created = await screen.findByRole('dialog', { name: 'Votre nouveau jeton' });
    expect(within(created).getByText(SECRET)).toBeInTheDocument();
    expect(sent).toEqual({ name: 'Claude sur mon laptop', preset: 'propose', expiresInDays: 90 });
    await expectNoAxeViolations(container);

    await user.click(within(created).getByRole('button', { name: 'J’ai copié le jeton' }));
    await waitFor(() => expect(screen.queryByText(SECRET)).toBeNull());
  });

  // Review Focus 4: what is copied is exactly the secret.
  it('copies exactly the secret', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    server.use(
      http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({ entries: [], next: null })),
      http.post('*/api/v1/core/me/tokens', () => HttpResponse.json({ token: token(), secret: SECRET }, { status: 201 })),
    );
    const { user } = await renderApp('/security');
    // After renderApp: user-event installs its own clipboard stub when it is set up.
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    await user.click(await screen.findByRole('button', { name: 'Créer un jeton' }));
    await user.type(screen.getByLabelText('Nom'), 'x');
    await user.click(screen.getByRole('button', { name: 'Créer' }));
    await user.click(await screen.findByRole('button', { name: 'Copier' }));

    await waitFor(() => expect(writeText).toHaveBeenCalledWith(SECRET));
    const createdDialog = screen.getByRole('dialog', { name: 'Votre nouveau jeton' });
    await waitFor(() => expect(within(createdDialog).getByRole('status')).toHaveTextContent('Jeton copié.'));
  });

  it('warns when « Écriture directe » is chosen', async () => {
    server.use(http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({ entries: [], next: null })));
    const { user } = await renderApp('/security');

    await user.click(await screen.findByRole('button', { name: 'Créer un jeton' }));
    expect(screen.queryByText(/peut être manipulé par son contenu/)).toBeNull();
    await user.click(screen.getByRole('radio', { name: /Écriture directe/ }));

    expect(screen.getByText(/peut être manipulé par son contenu/)).toBeInTheDocument();
  });

  it('says so when ten tokens are already active', async () => {
    server.use(
      http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({ entries: [], next: null })),
      http.post('*/api/v1/core/me/tokens', () => HttpResponse.json({ status: 409, code: 'core.token_limit_reached', title: 'x' }, { status: 409 })),
    );
    const { user } = await renderApp('/security');

    await user.click(await screen.findByRole('button', { name: 'Créer un jeton' }));
    await user.type(screen.getByLabelText('Nom'), 'x');
    await user.click(screen.getByRole('button', { name: 'Créer' }));

    expect(await screen.findByText(/déjà 10 jetons actifs/)).toBeInTheDocument();
  });

  it('revokes a token after a confirmation', async () => {
    let deleted = '';
    server.use(
      http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({ entries: [], next: null })),
      http.get('*/api/v1/core/me/tokens', () => HttpResponse.json([token()])),
      http.delete('*/api/v1/core/me/tokens/:id', ({ params }) => { deleted = String(params.id); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp('/security');

    const row = await screen.findByRole('row', { name: /Claude sur mon laptop/ });
    await user.click(within(row).getByRole('button', { name: 'Révoquer' }));
    await user.click(within(row).getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(deleted).toBe('t1'));
    await waitFor(() => expect(within(screen.getByRole('region', { name: 'Mes jetons' })).getByRole('status')).toHaveTextContent('Jeton révoqué.'));
  });
});
