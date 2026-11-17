import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { TaxYear } from '@/api/tax';
import { estimate, history } from '../taxTestData';

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
});
