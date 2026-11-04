import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { expiresSoon } from './tokens';

const base = { id: 't', name: 'n', preset: 'read', prefix: 'p', createdAt: '2026-10-01T00:00:00Z', lastUsedAt: null, revokedAt: null } as const;
const NOW = Date.parse('2026-10-16T00:00:00Z');

describe('expiresSoon', () => {
  it('is true for an active token within 7 days, false otherwise', () => {
    expect(expiresSoon({ ...base, status: 'active', expiresAt: '2026-10-22T23:00:00Z' }, NOW)).toBe(true);
    expect(expiresSoon({ ...base, status: 'active', expiresAt: '2026-10-24T00:00:00Z' }, NOW)).toBe(false);
    expect(expiresSoon({ ...base, status: 'revoked', expiresAt: '2026-10-17T00:00:00Z' }, NOW)).toBe(false);
    expect(expiresSoon({ ...base, status: 'expired', expiresAt: '2026-10-15T00:00:00Z' }, NOW)).toBe(false);
  });
});

describe('the token banner', () => {
  it('links to « Sécurité » when a token expires within 7 days', async () => {
    const soon = new Date(Date.now() + 2 * 86_400_000).toISOString();
    server.use(http.get('*/api/v1/core/me/tokens', () => HttpResponse.json([{ ...base, status: 'active', expiresAt: soon }])));
    const { container } = await renderApp('/');

    expect(await screen.findByText('Un jeton d’accès expire bientôt.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Voir mes jetons' })).toHaveAttribute('href', '/security');
    await expectNoAxeViolations(container);
  });

  // « Jamais sur le fond nu »: a pane like the content under it.
  it('sits on a material pane, never the bare ground', async () => {
    const soon = new Date(Date.now() + 2 * 86_400_000).toISOString();
    server.use(http.get('*/api/v1/core/me/tokens', () => HttpResponse.json([{ ...base, status: 'active', expiresAt: soon }])));
    await renderApp('/');

    expect(await screen.findByRole('note')).toHaveClass('material', 'rounded-pane');
  });

  it('stays hidden otherwise', async () => {
    await renderApp('/');
    await screen.findByRole('heading', { level: 1, name: 'Accueil' });
    expect(screen.queryByText('Un jeton d’accès expire bientôt.')).toBeNull();
  });
});
