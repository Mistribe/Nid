import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { yearsAgoIso } from '@/lib/today';

type Point = { date: string; value: string; base100?: string | null };
const item = (key: string, kind: string, name: string | null, unit: string, points: Point[], frequency = 'monthly') => ({ key, kind, name, unit, frequency, item: null, points });
const cpi = item('cpi', 'cpi', null, 'index', [
  { date: '2025-01-01', value: '120.1', base100: '100' }, { date: '2025-02-01', value: '120.5', base100: '100.33' }, { date: '2025-03-01', value: '121', base100: '100.75' },
]);
const yoy = item('cpi:yoy', 'cpi_yoy', null, 'percent', [{ date: '2025-03-01', value: '2.1', base100: null }]);
const fx = item('fx:EUR', 'fx', 'EUR', 'rate:EUR', [
  { date: '2025-01-01', value: '0.86', base100: '100' }, { date: '2025-03-01', value: '0.87', base100: '101.16' },
], 'daily');

const info = (key: string, kind: string, name: string | null, unit: string) => ({ key, kind, name, unit, frequency: 'monthly' });
const catalogue = (items: unknown[], total = items.length) => HttpResponse.json({ items, total, page: 1 });
const answer = (series: unknown[], baseDate: string | null = null) => HttpResponse.json({ series, baseDate });

describe('Explorer des séries', () => {
  it('asks the server for the keys of the URL over five years', async () => {
    let url: URL | undefined;
    server.use(http.get('*/api/v1/series/points', ({ request }) => { url = new URL(request.url); return answer([cpi]); }));
    const { container } = await renderApp('/series?s=cpi,irl');
    expect(await screen.findByRole('heading', { name: 'Explorer les séries' })).toBeInTheDocument();
    await waitFor(() => expect(url).toBeDefined());
    expect(url?.searchParams.get('s')).toBe('cpi,irl');
    expect(url?.searchParams.get('from')).toBe(yearsAgoIso(5));
    expect(url?.searchParams.get('scope')).toBe('household');
    expect(url?.searchParams.has('to')).toBe(false);
    await expectNoAxeViolations(container);
  });

  it('switches to the table and back through the URL', async () => {
    server.use(http.get('*/api/v1/series/points', () => answer([cpi])));
    const { user, router } = await renderApp('/series?s=cpi');
    await user.click(await screen.findByRole('button', { name: 'Afficher en tableau' }));
    expect(router.state.location.search).toMatchObject({ view: 'table' });
    expect(await screen.findByRole('columnheader', { name: 'Indice des prix (IPC) (indice)' })).toBeInTheDocument();
    expect(screen.getAllByRole('row')).toHaveLength(4);
    await user.click(screen.getByRole('button', { name: 'Afficher le graphique' }));
    await waitFor(() => expect((router.state.location.search as { view?: string }).view).toBeUndefined());
  });

  it('draws a single point as a dot', async () => {
    server.use(http.get('*/api/v1/series/points', () => answer([item('cpi', 'cpi', null, 'index', [cpi.points[0] as Point])])));
    const { container } = await renderApp('/series?s=cpi');
    expect(await screen.findByRole('button', { name: 'Afficher en tableau' })).toBeInTheDocument();
    await waitFor(() => expect(container.querySelectorAll('.recharts-dot').length).toBe(1));
    expect(within(container.querySelector('div.sr-only') as HTMLElement).getAllByRole('row')).toHaveLength(2);
  });

  it('keeps the base date and the missing-base note in the table view', async () => {
    server.use(http.get('*/api/v1/series/points', () => answer([cpi, fx, yoy], '2021-01-01')));
    await renderApp('/series?s=cpi,fx:EUR,cpi:yoy&view=table');
    expect(await screen.findByText('Base 100 le 1 janv. 2021')).toBeInTheDocument();
    expect(screen.getByText(/pas de base 100 possible/)).toBeInTheDocument();
  });

  it('labels each year once on the time axis', async () => {
    server.use(http.get('*/api/v1/series/points', () => answer([item('cpi', 'cpi', null, 'index', [
      { date: '2022-03-01', value: '1' }, { date: '2023-06-01', value: '2' }, { date: '2024-02-01', value: '3' }])])));
    const { container } = await renderApp('/series?s=cpi');
    await screen.findByRole('button', { name: 'Afficher en tableau' });
    await new Promise((r) => setTimeout(r, 500));
    const labels = [...container.querySelectorAll('.recharts-xAxis-tick-labels .recharts-cartesian-axis-tick-value')].map((n) => n.textContent);
    expect(labels).toEqual(['2023', '2024']);
  });

  it('says when the period holds no data', async () => {
    server.use(http.get('*/api/v1/series/points', () => answer([item('cpi', 'cpi', null, 'index', [])])));
    await renderApp('/series?s=cpi');
    expect(await screen.findByText('Aucune donnée sur cette période')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Afficher en tableau' })).not.toBeInTheDocument();
  });

  it('puts a percent series on a second axis', async () => {
    server.use(http.get('*/api/v1/series/points', () => answer([cpi, yoy])));
    const { container } = await renderApp('/series?s=cpi,cpi:yoy');
    await waitFor(() => expect(screen.getByRole('list', { name: 'Séries affichées' })).toHaveTextContent('Inflation sur 12 mois'));
    await waitFor(() => expect(container.querySelectorAll('.recharts-yAxis')).toHaveLength(2));
    expect(screen.queryByText(/Base 100 le/)).not.toBeInTheDocument();
  });

  it('rebases two units without a percent', async () => {
    server.use(http.get('*/api/v1/series/points', () => answer([cpi, fx], '2021-01-01')));
    const { user } = await renderApp('/series?s=cpi,fx:EUR');
    expect(await screen.findByText('Base 100 le 1 janv. 2021')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Afficher en tableau' }));
    expect(await screen.findByRole('columnheader', { name: 'Indice des prix (IPC) (base 100)' })).toBeInTheDocument();
  });

  it('names a percent series it cannot rebase in a three-unit mix', async () => {
    server.use(http.get('*/api/v1/series/points', () => answer([cpi, fx, yoy], '2021-01-01')));
    const { container } = await renderApp('/series?s=cpi,fx:EUR,cpi:yoy');
    await waitFor(() => expect(screen.getByRole('list', { name: 'Séries affichées' })).toHaveTextContent('Inflation sur 12 mois'));
    expect(screen.getByRole('list', { name: 'Séries affichées' })).toHaveTextContent('pas de base 100 possible');
    await waitFor(() => expect(container.querySelectorAll('.recharts-line')).toHaveLength(2));
  });

  it('asks for the series named by the typed text, on the server', async () => {
    const asked: URLSearchParams[] = [];
    server.use(
      http.get('*/api/v1/series', ({ request }) => { asked.push(new URL(request.url).searchParams); return catalogue([]); }),
      http.get('*/api/v1/series/points', () => answer([cpi])));
    const { user } = await renderApp('/series?s=cpi');
    await user.click(await screen.findByRole('combobox', { name: 'Ajouter une série' }));
    await user.type(await screen.findByRole('combobox', { name: 'Rechercher une série…' }), 'loyer');
    await waitFor(() => expect(asked.some((a) => a.get('q') === 'loyer' && a.get('kinds') === 'irl')).toBe(true));
  });

  it('adds a series from the catalogue searched on the server, replacing the URL', async () => {
    let q: string | null = null;
    server.use(
      http.get('*/api/v1/series', ({ request }) => { q = new URL(request.url).searchParams.get('q'); return catalogue([info('irl', 'irl', null, 'index')]); }),
      http.get('*/api/v1/series/points', () => answer([cpi])));
    const { user, router } = await renderApp('/series?s=cpi');
    const before = router.history.length;
    await user.click(await screen.findByRole('combobox', { name: 'Ajouter une série' }));
    await user.type(await screen.findByRole('combobox', { name: 'Rechercher une série…' }), 'irl');
    await waitFor(() => expect(q).toBe('irl'));
    await user.click(await screen.findByRole('option', { name: 'Indice de référence des loyers (IRL)' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ s: 'cpi,irl' }));
    expect(router.history.length).toBe(before);
  });

  it('removes a chip, and a stale chip removes nothing more', async () => {
    server.use(http.get('*/api/v1/series/points', () => answer([cpi, fx])));
    const { user, router } = await renderApp('/series?s=cpi,fx:EUR');
    const remove = await screen.findByRole('button', { name: 'Retirer Indice des prix (IPC)' });
    await user.click(remove);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ s: 'fx:EUR' }));
    await user.click(remove);
    expect(router.state.location.search).toMatchObject({ s: 'fx:EUR' });
  });

  it('disables a percent option in a three-unit mix, with its reason', async () => {
    server.use(http.get('*/api/v1/series', () => catalogue([info('cpi:yoy', 'cpi_yoy', null, 'percent')])),
      http.get('*/api/v1/series/points', () => answer([cpi, fx])));
    const { user } = await renderApp('/series?s=cpi,fx:EUR');
    await user.click(await screen.findByRole('combobox', { name: 'Ajouter une série' }));
    expect(await screen.findByRole('option', { name: /Inflation sur 12 mois — un pourcentage ne se compare pas/ })).toHaveAttribute('aria-disabled', 'true');
  });

  it('disables a third unit beside a percent', async () => {
    server.use(http.get('*/api/v1/series', () => catalogue([info('fx:EUR', 'fx', 'EUR', 'rate:EUR')])),
      http.get('*/api/v1/series/points', () => answer([cpi, yoy])));
    const { user } = await renderApp('/series?s=cpi,cpi:yoy');
    await user.click(await screen.findByRole('combobox', { name: 'Ajouter une série' }));
    expect(await screen.findByRole('option', { name: /EUR pour 1 USD — une troisième unité/ })).toHaveAttribute('aria-disabled', 'true');
  });

  it('marks a selected series as already shown', async () => {
    server.use(http.get('*/api/v1/series', () => catalogue([info('cpi', 'cpi', null, 'index')])),
      http.get('*/api/v1/series/points', () => answer([cpi])));
    const { user } = await renderApp('/series?s=cpi');
    await user.click(await screen.findByRole('combobox', { name: 'Ajouter une série' }));
    expect(await screen.findByRole('option', { name: /Indice des prix \(IPC\) — déjà affichée/ })).toHaveAttribute('aria-disabled', 'true');
  });

  it('disables the combobox at six series', async () => {
    const six = ['a', 'b', 'c', 'd', 'e', 'f'].map((k) => item(k, 'unknown', k, 'index', [{ date: '2025-01-01', value: '1' }]));
    server.use(http.get('*/api/v1/series/points', () => answer(six)));
    await renderApp('/series?s=a,b,c,d,e,f');
    expect(await screen.findByRole('combobox', { name: 'Ajouter une série' })).toBeDisabled();
    expect(screen.getByText('6 séries au plus')).toBeInTheDocument();
  });

  it('says there are more series than listed', async () => {
    server.use(http.get('*/api/v1/series', () => catalogue([info('irl', 'irl', null, 'index')], 51)),
      http.get('*/api/v1/series/points', () => answer([cpi])));
    const { user } = await renderApp('/series?s=cpi');
    await user.click(await screen.findByRole('combobox', { name: 'Ajouter une série' }));
    expect(await screen.findByText(/Précisez la recherche/)).toBeInTheDocument();
  });

  it('invites and focuses the combobox without s, and after the last chip is removed', async () => {
    let asked = 0;
    server.use(http.get('*/api/v1/series/points', () => { asked += 1; return answer([cpi]); }));
    const first = await renderApp('/series');
    expect(await screen.findByText(/Choisissez une série/)).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Ajouter une série' })).toHaveFocus();
    expect(asked).toBe(0);
    first.unmount();

    const { user } = await renderApp('/series?s=cpi');
    await user.click(await screen.findByRole('button', { name: 'Retirer Indice des prix (IPC)' }));
    expect(await screen.findByText(/Choisissez une série/)).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Ajouter une série' })).toHaveFocus();
    expect(screen.queryByRole('list', { name: 'Séries affichées' })).not.toBeInTheDocument();
  });

  it('drops a key the server does not know and says so', async () => {
    server.use(http.get('*/api/v1/series/points', ({ request }) => new URL(request.url).searchParams.get('s')?.includes('nope')
      ? HttpResponse.json({ status: 404, code: 'core.series_not_found', title: 'Not found.', key: 'nope' }, { status: 404 })
      : answer([cpi])));
    const { router } = await renderApp('/series?s=cpi,nope');
    await waitFor(() => expect(router.state.location.search).toMatchObject({ s: 'cpi' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Une série de l’adresse');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows any other error', async () => {
    server.use(http.get('*/api/v1/series/points', () => HttpResponse.json({ status: 500, title: 'Boom' }, { status: 500 })));
    await renderApp('/series?s=cpi');
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
