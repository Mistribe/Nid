import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const call = (over: Record<string, unknown> = {}) => ({
  id: 'c1', createdAt: '2026-11-22T08:00:00+00:00', use: 'chat', model: 'claude-opus-5-5', destination: 'anthropic', destinationKind: 'cloud',
  tokensIn: 1200, tokensOut: 300, costEur: '0.0108', status: 'succeeded', code: null, ...over,
});

const totals = { calls: 3, tokensIn: 2000, tokensOut: 500, costEur: '0.0208' };

describe('Réglages › IA › Utilisation', () => {
  it("lists the reader's calls with where they ran and what they cost, and the household's month", async () => {
    server.use(http.get('*/api/v1/ai/usage', () => HttpResponse.json({
      items: [call(), call({ id: 'c2', destination: 'Serveur du salon', destinationKind: 'worker', model: 'llama3.2', costEur: null, status: 'blocked_budget', code: 'ai.budget_blocked' })],
      total: 2, page: 1, month: '2026-11-01', totals,
    })));
    const { container } = await renderApp('/settings/ai/usage');

    const cloud = await screen.findByRole('row', { name: /Anthropic/ });
    expect(cloud).toHaveTextContent('Assistant');
    expect(cloud).toHaveTextContent(/0,0108\s€/);
    expect(cloud).toHaveTextContent('Réussi');
    expect(screen.getByRole('row', { name: /Serveur du salon/ })).toHaveTextContent("Le budget du mois des fournisseurs payants est atteint");
    expect(screen.getByText(/Ce mois-ci, pour le foyer : 3 appels, 2\s500 jetons, 0,0208\s€\./)).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('sends the page to the server', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/ai/usage', ({ request }) => {
      seen.push(new URL(request.url).search);
      return HttpResponse.json({ items: [call()], total: 51, page: 1, month: '2026-11-01', totals });
    }));
    const { user } = await renderApp('/settings/ai/usage');

    const pages = await screen.findByRole('navigation', { name: 'Pages' });
    await user.click(within(pages).getByRole('button', { name: 'Suivant' }));

    await waitFor(() => expect(seen.at(-1)).toBe('?page=2'));
  });
});
