import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { NetWorthHistory } from './api';
import { curvePoint, dashboard, netWorth } from './testData';

// The default dashboard handler serves an empty household (welcome state): the curve needs envelopes.
function serveCurve(body: NetWorthHistory, seen: string[] = []) {
  server.use(
    http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())),
    http.get('*/api/v1/dashboard/net-worth', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(body); }),
  );
}

const curve = () => screen.findByRole('region', { name: 'Évolution du patrimoine' });

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./NetWorthCurve'); }, 30_000);

describe('net worth curve', () => {
  it('switches period in its header: asks for twelve months in the scope by default and keeps the choice in the URL', async () => {
    const seen: string[] = [];
    serveCurve(netWorth([curvePoint({ on: '2025-08-31' }), curvePoint({ on: '2025-10-15' })]), seen);
    const { router, user } = await renderApp('/dashboard?scope=me');

    const card = await curve();
    expect(await within(card).findByRole('radio', { name: '12 mois' })).toBeChecked();
    await within(card).findByRole('button', { name: 'Afficher en tableau' });
    expect(seen[0]).toContain('period=12m');
    expect(seen[0]).toContain('scope=me');

    await user.click(within(card).getByRole('radio', { name: '3 ans' }));
    expect(router.state.location.search).toEqual({ scope: 'me', curve: '3y' });
    await expect.poll(() => seen.some((s) => s.includes('period=3y'))).toBe(true);
  });

  it('shows the table with an unknown month and today last', async () => {
    serveCurve(netWorth([
      curvePoint({ on: '2025-08-31', value: null, contributions: null, missingCurrency: 'USD' }),
      curvePoint({ on: '2025-09-30' }),
      curvePoint({ on: '2025-10-15', value: { amount: '1100.00', currency: 'EUR' } }),
    ]));
    const { container, user } = await renderApp('/dashboard');

    await user.click(await screen.findByRole('button', { name: 'Afficher en tableau' }));
    const rows = within(screen.getByRole('table', { name: 'Évolution du patrimoine en tableau' })).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('août 2025');
    expect(rows[1]).toHaveTextContent('Inconnue : taux de change manquant (USD)');
    expect(rows[2]).toHaveTextContent('1 000,00 €');
    expect(rows[2]).toHaveTextContent('900,00 €');
    expect(rows[3]).toHaveTextContent('Aujourd’hui');
    expect(rows[3]).toHaveTextContent('1 100,00 €');
    await expectNoAxeViolations(container);
  });

  it('swaps the chart for its table in place and back', async () => {
    serveCurve(netWorth([curvePoint({ on: '2025-08-31' }), curvePoint({ on: '2025-10-15' })]));
    const { user } = await renderApp('/dashboard');

    const card = await curve();
    await user.click(await within(card).findByRole('button', { name: 'Afficher en tableau' }));
    expect(within(card).getByRole('table', { name: 'Évolution du patrimoine en tableau' })).toBeInTheDocument();
    expect(within(card).getByRole('radio', { name: '3 ans' })).toBeInTheDocument();
    await user.click(within(card).getByRole('button', { name: 'Afficher le graphique' }));
    expect(within(card).queryByRole('table')).not.toBeInTheDocument();
  });

  it('says when no point has a value', async () => {
    serveCurve(netWorth([curvePoint({ value: null, contributions: null })]));
    await renderApp('/dashboard');
    expect(await within(await curve()).findByText('Aucune valeur sur cette période.')).toBeInTheDocument();
  });

  it('shows a single known point as a table, without the chart', async () => {
    serveCurve(netWorth([curvePoint({ on: '2025-10-15' })]));
    await renderApp('/dashboard');
    const card = await curve();
    expect(await within(card).findByRole('table', { name: 'Évolution du patrimoine en tableau' })).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Afficher le graphique' })).not.toBeInTheDocument();
  });

  it('skips the empty months before the first value in the table', async () => {
    serveCurve(netWorth([
      curvePoint({ on: '2025-08-31', value: null, contributions: null }),
      curvePoint({ on: '2025-09-30', value: null, contributions: null }),
      curvePoint({ on: '2025-10-15' }),
    ]));
    await renderApp('/dashboard');
    const rows = within(await within(await curve()).findByRole('table', { name: 'Évolution du patrimoine en tableau' })).getAllByRole('row');
    expect(rows).toHaveLength(2);
    expect(rows[1]).toHaveTextContent('Aujourd’hui');
  });

  it('is not shown without any envelope', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard({ envelopes: 0, envelopeSplit: [], people: [] }))));
    await renderApp('/dashboard');
    await screen.findByRole('button', { name: /Patrimoine net/ });
    expect(screen.queryByRole('region', { name: 'Évolution du patrimoine' })).not.toBeInTheDocument();
  });
});
