import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { TaxYear } from '@/api/tax';
import { ADULT, estimate, history } from '../taxTestData';

const load = (rows: TaxYear[] = history()) => {
  const years: (string | null)[] = [];
  const historyCalls: string[] = [];
  server.use(
    http.get('*/api/v1/tax', ({ request }) => { years.push(new URL(request.url).searchParams.get('year')); return HttpResponse.json({ ...estimate(), history: rows }); }),
    http.get('*/api/v1/tax/changes', () => HttpResponse.json({ year: 2026, lines: [] })),
    http.get('*/api/v1/tax/history', () => { historyCalls.push('history'); return HttpResponse.json({ years: rows }); }),
  );
  return { years, historyCalls };
};
const card = () => screen.findByRole('region', { name: 'Impôt année par année' });
const texts = (c: HTMLElement, sel: string) => [...c.querySelectorAll(sel)].map((n) => n.textContent);

describe('EvolutionTab', () => {
  beforeAll(async () => { await import('../TaxPage'); }, 30_000);

  it('draws one bar per year of the server’s history', async () => {
    load();
    await renderApp('/tax?tab=evolution');

    const region = await card();
    await waitFor(() => expect(region.querySelectorAll('.recharts-bar-rectangle')).toHaveLength(3));
    expect(texts(region, '.recharts-xAxis-tick-labels .recharts-cartesian-axis-tick-value')).toEqual(['2024', '2025', '2026']);
    await waitFor(() => expect(region.querySelectorAll('.recharts-bar-rectangle path')).toHaveLength(3));
    const paths = [...region.querySelectorAll('.recharts-bar-rectangle path')];
    expect(paths.map((p) => p.getAttribute('fill-opacity'))).toEqual([null, '0.4', '0.4']);
    expect(paths[2]!.getAttribute('stroke')).toBe('var(--foreground)');
    expect(texts(region, '.recharts-label-list text').filter(Boolean).map((x) => x?.replace(/\s/g, ' '))).toEqual(['4,8 %', '4,5 %', '0,38 %']);
    expect(within(region).getByText('Avis')).toBeInTheDocument();
    expect(within(region).getByText('Estimation')).toBeInTheDocument();
  });

  it('reads no other year’s estimate', async () => {
    const { years, historyCalls } = load();
    await renderApp('/tax?tab=evolution');

    await waitFor(() => expect(document.querySelectorAll('.recharts-bar-rectangle')).toHaveLength(3));
    expect(new Set(years)).toEqual(new Set(['2026']));
    expect(historyCalls).toEqual([]);
  });

  it('shows the same years as a table, the selected one current', async () => {
    load();
    const { user } = await renderApp('/tax?tab=evolution');

    await user.click(await screen.findByRole('button', { name: 'Afficher en tableau' }));
    const table = await screen.findByRole('table', { name: 'Impôt année par année' });
    const row = within(table).getByRole('row', { name: /^2026/ });
    expect(row).toHaveTextContent(/64 407,00 €247,00 €0,38 %Estimation/);
    expect(row).toHaveAttribute('aria-current', 'true');
  });

  it('skips a year the server does not list', async () => {
    load(history().map((r, i) => ({ ...r, year: [2023, 2025, 2026][i]! })));
    await renderApp('/tax?tab=evolution');

    const region = await card();
    await waitFor(() => expect(texts(region, '.recharts-xAxis-tick-labels .recharts-cartesian-axis-tick-value')).toEqual(['2023', '2025', '2026']));
  });

  it('a negative year reads in your favour', async () => {
    load(history().map((r) => (r.year === 2026 ? { ...r, tax: '-150.00' } : r)));
    const { user } = await renderApp('/tax?tab=evolution');

    await user.click(await screen.findByRole('button', { name: 'Afficher en tableau' }));
    const table = await screen.findByRole('table', { name: 'Impôt année par année' });
    expect(within(table).getByRole('row', { name: /^2026/ })).toHaveTextContent('150,00 € en votre faveur');
  });

  it('links the notices to the explorer only when a notice is listed', async () => {
    load();
    const { user, router } = await renderApp('/tax?tab=evolution');
    await user.click(await screen.findByRole('link', { name: 'Voir la série' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/series'));
    expect(router.state.location.search).toMatchObject({ s: 'tax:yearly' });
    cleanup();

    load(history().map((r) => ({ ...r, source: 'estimate' as const })));
    await renderApp('/tax?tab=evolution');
    await card();
    expect(screen.queryByRole('link', { name: 'Voir la série' })).not.toBeInTheDocument();
  });

  it('has no accessibility violation', async () => {
    load();
    const { container } = await renderApp('/tax?tab=evolution');
    await waitFor(() => expect(container.querySelectorAll('.recharts-bar-rectangle')).toHaveLength(3));
    await expectNoAxeViolations(container);
  });

  describe('what changed since last year', () => {
    const changed = (adults = true) => {
      const loaded = estimate();
      if (!adults) loaded.adults = [];
      server.use(
        http.get('*/api/v1/tax', () => HttpResponse.json(loaded)),
        http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: ADULT, displayName: 'Alice', kind: 'adult' }])),
        http.get('*/api/v1/tax/changes', () => HttpResponse.json({
          year: 2026, taxBefore: '2500.00', taxBeforeFromNotice: true, taxNow: '6604.00', remainder: '-396.00',
          lines: [
            { code: 'salaries', person: ADULT, before: '30000.00', now: '45000.00', effect: '4500.00', fromNotice: true },
            { code: 'shares', person: null, before: '1.00', now: '1.50', effect: '-1807.00', fromNotice: false },
          ],
        })),
      );
    };
    const region = () => screen.findByRole('region', { name: 'Ce qui a changé depuis 2025' });

    it('draws each change around zero, a rise and a cut in their status', async () => {
      changed();
      await renderApp('/tax?tab=evolution');

      const r = await region();
      await waitFor(() => expect(r.querySelectorAll('.recharts-bar-rectangle path')).toHaveLength(2));
      expect([...r.querySelectorAll('.recharts-bar-rectangle path')].map((p) => p.getAttribute('fill'))).toEqual(['var(--status-critical)', 'var(--status-good)']);
      expect(texts(r, '.recharts-yAxis-tick-labels .recharts-cartesian-axis-tick-value')).toEqual(['Salaires nets — Alice', 'Parts']);
    });

    it('keeps the table under the chart', async () => {
      changed();
      const { container } = await renderApp('/tax?tab=evolution');

      const rows = within(await screen.findByRole('table', { name: 'Ce qui a changé depuis 2025' })).getAllByRole('row');
      expect(rows[1]).toHaveTextContent(/^Salaires nets — Alice30 000,00 €avis45 000,00 €\+4 500,00 €$/);
      expect(rows[2]).toHaveTextContent(/^Parts11,5-1 807,00 €$/);
      expect(rows[3]).toHaveTextContent(/barème, plafonds, arrondis-396,00 €$/);
      expect(rows[4]).toHaveTextContent(/2 500,00 €avis6 604,00 €$/);
      expect(screen.getByText(/^Indicatif/)).toBeInTheDocument();
      await expectNoAxeViolations(container);
    });

    it('names a person from the household’s persons', async () => {
      changed(false);
      await renderApp('/tax?tab=evolution');

      const table = await screen.findByRole('table', { name: 'Ce qui a changé depuis 2025' });
      expect(within(table).getAllByRole('row')[1]).toHaveTextContent(/^Salaires nets — Alice/);
    });
  });
});
