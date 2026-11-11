import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { SERIES_POINTS } from '@/api/series';
import { server } from '@/test/server';

type Body = Record<string, unknown>;

const ASSUMPTIONS = '*/api/v1/core/projection-assumptions';

const assumptions = (over: Body = {}) => ({
  classRates: { cash: '1.5', money_market: '2', bond: '3', equity: '6', mixed: '4', capital_guaranteed: '2.5', real_estate: '2', other: '0' },
  inflation: '2', monthlySavings: null, savingsClass: 'equity', freedPaymentsToSavings: true, currency: 'EUR', inflationByYear: {}, classRatesByYear: {}, incomeGrowth: '0', withdrawalRate: '4', monthlyLivingCost: '0.00',
  classVolatility: { cash: '0', money_market: '0', bond: '6', equity: '16', mixed: '10', capital_guaranteed: '0', real_estate: '10', other: '0' },
  observedInflation: null, ...over,
});

const observed = {
  lastPublished: '2026-09', last12Months: { to: '2026-09', percent: '1.2' },
  years: [{ year: 2025, percent: '0.9' }, { year: 2024, percent: '2.0' }],
};

const YEAR = new Date().getFullYear();
const NEXT = YEAR + 1;

function serve(initial: Body = assumptions(), inflation: Body = observed) {
  const puts: Body[] = [];
  server.use(
    http.get(ASSUMPTIONS, () => HttpResponse.json(initial)),
    http.put(ASSUMPTIONS, async ({ request }) => { const body = (await request.json()) as Body; puts.push(body); return HttpResponse.json(body); }),
    http.get('*/api/v1/core/inflation', () => HttpResponse.json(inflation)),
  );
  return { puts };
}

/** A class's cells in the returns table: class, rate, volatility. */
const classRow = async (name: string) => within(await screen.findByRole('row', { name: new RegExp(`^${name} `) })).getAllByRole('cell').map((c) => c.textContent);
/** The figure beside an assumption's label. */
const figure = async (label: string) => (await screen.findByText(label, { selector: 'dt > span' })).closest('div')!.querySelector('dd')!.textContent;

beforeAll(async () => { await import('./ProjectionSettingsPage'); }, 30_000);

describe('the projection settings page', () => {
  it('shows the household assumptions and links to the persons for retirement', async () => {
    serve();
    await renderApp('/settings/projection');

    expect(await screen.findByRole('heading', { level: 1, name: 'Réglages' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Projection' })).toHaveAttribute('aria-current', 'page');
    expect(await classRow('Actions')).toEqual(['Actions', '6 %', '16 %']);
    expect(await classRow('Liquidités')).toEqual(['Liquidités', '1,5 %', '0 %']);
    expect(screen.getByText(/Automatique \(moyenne des 12 derniers mois\)/)).toBeInTheDocument();
    expect(await figure('Mensualités terminées versées à l’épargne')).toBe('oui');
    // #156: birth dates and retirement ages moved to Réglages › Personnes.
    expect(screen.queryByRole('region', { name: 'Retraite' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Ouvrir Réglages › Personnes' })).toHaveAttribute('href', '/settings/persons');
  });

  it('saves new rates and sends percent strings', async () => {
    const { puts } = serve();
    const { user, queryClient } = await renderApp('/settings/projection');
    queryClient.setQueryData(['get', '/api/v1/projection'], {});
    queryClient.setQueryData(['get', SERIES_POINTS], {});

    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    const equity = await screen.findByLabelText('Actions');
    await user.clear(equity);
    await user.type(equity, '7');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect((puts[0]!.classRates as Body).equity).toBe('7');
    expect(puts[0]).not.toHaveProperty('currency');
    await waitFor(() => expect(queryClient.getQueryState(['get', '/api/v1/projection'])?.isInvalidated).toBe(true));
    expect(queryClient.getQueryState(['get', SERIES_POINTS])?.isInvalidated).toBe(true);
    expect(puts[0]).toMatchObject({ inflation: '2', monthlySavings: null, savingsClass: 'equity', freedPaymentsToSavings: true });
  });

  it('lists and sends the volatility of each class', async () => { // ranges spec §2
    const { puts } = serve();
    const { user } = await renderApp('/settings/projection');

    expect((await classRow('Obligations'))[2]).toBe('6 %');
    await user.click(screen.getByRole('button', { name: 'Modifier les hypothèses' }));
    const equity = await screen.findByLabelText('Volatilité Actions');
    expect(equity).toHaveValue('16');
    await user.clear(equity);
    await user.type(equity, '18,5');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]!.classVolatility).toMatchObject({ equity: '18.5', bond: '6', cash: '0' });
  });

  it('shows the fixed savings in the household currency', async () => {
    serve(assumptions({ monthlySavings: '500.00', currency: 'CHF' }));
    const { user } = await renderApp('/settings/projection');

    expect(await figure('Épargne mensuelle')).toMatch(/^500,00\s*CHF$/);
    await user.click(screen.getByRole('button', { name: 'Modifier les hypothèses' }));
    expect(await screen.findByLabelText('Épargne mensuelle (CHF)')).toHaveValue('500,00');
  });

  it('shows and sends the withdrawal rate and the living cost', async () => { // retirement spec §3
    const { puts } = serve(assumptions({ withdrawalRate: '3.5', monthlyLivingCost: '2500.00' }));
    const { user } = await renderApp('/settings/projection');

    expect(await figure('Taux de retrait')).toBe('3,5 %');
    expect(await figure('Coût de la vie mensuel')).toMatch(/^2\s?500,00\s*€$/);
    await user.click(screen.getByRole('button', { name: 'Modifier les hypothèses' }));
    const rate = await screen.findByLabelText('Taux de retrait');
    await user.clear(rate);
    await user.type(rate, '3,5');
    const cost = screen.getByLabelText('Coût de la vie mensuel (EUR)');
    await user.clear(cost);
    await user.type(cost, '2500');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toMatchObject({ withdrawalRate: '3.5', monthlyLivingCost: '2500' });
  });

  it('shows an unset living cost as not filled in', async () => {
    serve();
    await renderApp('/settings/projection');

    expect(await figure('Coût de la vie mensuel')).toBe('non renseigné');
  });

  it('sends a fixed monthly amount', async () => {
    const { puts } = serve();
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    await user.click(await screen.findByRole('radio', { name: 'Montant fixe' }));
    await user.type(screen.getByLabelText('Épargne mensuelle (EUR)'), '500');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]!.monthlySavings).toBe('500');
  });

  it('shows the server field error on its field', async () => {
    serve();
    server.use(http.put(ASSUMPTIONS, () => HttpResponse.json(
      { status: 422, code: 'projection.invalid_field', errors: [{ path: 'classRates.equity', code: 'projection.invalid_field', message: 'Taux hors limites.' }] },
      { status: 422, headers: { 'content-type': 'application/problem+json' } })));
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    await user.click(await screen.findByRole('button', { name: 'Enregistrer' }));

    const field = await screen.findByLabelText('Actions');
    await waitFor(() => expect(field).toHaveAccessibleDescription('Taux hors limites.'));
    expect(field).toBeInvalid();
  });

  it('sends only the typed years and the income growth', async () => {
    const { puts } = serve(assumptions({ observedInflation: { percent: '3.1', to: '2026-09' } }));
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    expect(await screen.findByText('observée : 3,1 % (12 mois à fin septembre 2026)')).toBeInTheDocument();
    await user.type(screen.getByLabelText(`Inflation ${YEAR}`), '2,5');
    await user.type(screen.getByLabelText(`Liquidités ${NEXT}`), '3');
    const growth = screen.getByLabelText('Évolution des revenus : inflation + x %');
    await user.clear(growth);
    await user.type(growth, '0,5');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toMatchObject({ inflationByYear: { [YEAR]: '2.5' }, classRatesByYear: { cash: { [NEXT]: '3' } }, incomeGrowth: '0.5' });
    expect(puts[0]!.classRatesByYear).not.toHaveProperty('money_market');
  });

  it('reopens the dialog with the saved years and income growth', async () => {
    serve(assumptions({ inflationByYear: { [YEAR]: '2.5' }, classRatesByYear: { money_market: { [NEXT]: '3.2' } }, incomeGrowth: '0.5', observedInflation: { percent: '3.1', to: '2026-09' } }));
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    expect(await screen.findByLabelText(`Inflation ${YEAR}`)).toHaveValue('2,5');
    expect(screen.getByLabelText(`Monétaire ${NEXT}`)).toHaveValue('3,2');
    expect(screen.getByLabelText('Évolution des revenus : inflation + x %')).toHaveValue('0,5');
    expect(screen.queryByText(/observée : 3,1/)).not.toBeInTheDocument();
  });

  it('lists the typed years in the summary', async () => {
    serve(assumptions({ inflationByYear: { [YEAR]: '2.5', [NEXT]: '2.2' }, classRatesByYear: { cash: { [YEAR]: '3' } }, incomeGrowth: '0.5' }));
    await renderApp('/settings/projection');

    expect(await figure('Inflation')).toBe(`${YEAR} 2,5 %, ${NEXT} 2,2 %, puis 2 %`);
    expect((await classRow('Liquidités'))[1]).toBe(`${YEAR} 3 %, puis 1,5 %`);
    expect(await figure('Évolution des revenus')).toBe('inflation + 0,5 %');
  });

  it('shows a yearly field error on its field', async () => {
    serve();
    server.use(http.put(ASSUMPTIONS, () => HttpResponse.json(
      { status: 422, code: 'projection.invalid_field', errors: [{ path: `classRatesByYear.cash.${NEXT}`, code: 'projection.invalid_field', message: 'Taux hors limites.' }] },
      { status: 422, headers: { 'content-type': 'application/problem+json' } })));
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    await user.click(await screen.findByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(screen.getByLabelText(`Liquidités ${NEXT}`)).toHaveAccessibleDescription('Taux hors limites.'));
  });

  it('has no axe violations', async () => {
    serve();
    const { container } = await renderApp('/settings/projection');
    await classRow('Actions');
    await expectNoAxeViolations(container);
  });

  it('explains each assumption in a tooltip, on hover and on keyboard focus', async () => {
    serve();
    const { user } = await renderApp('/settings/projection');

    await classRow('Actions');
    const section = screen.getByRole('region', { name: 'Hypothèses' });
    const tips = within(section).getAllByRole('button', { name: /^À propos : / }).map((b) => b.getAttribute('aria-label'));
    expect(tips).toEqual(['Rendement annuel', 'Volatilité', 'Inflation', 'Évolution des revenus', 'Épargne mensuelle', 'Classe d’actifs de l’épargne',
      'Mensualités terminées versées à l’épargne', 'Taux de retrait', 'Coût de la vie mensuel'].map((n) => `À propos : ${n}`));

    const rate = within(section).getByRole('button', { name: 'À propos : Taux de retrait' });
    await user.hover(rate);
    expect(await screen.findByRole('tooltip')).toHaveTextContent(/date d’indépendance financière/);
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('tooltip')).not.toBeInTheDocument());

    within(section).getByRole('button', { name: 'À propos : Inflation' }).focus();
    expect(await screen.findByRole('tooltip')).toHaveTextContent(/inflation observée sur 12 mois/);
    expect(within(section).getByRole('button', { name: 'À propos : Inflation' })).toHaveAccessibleDescription(/Hausse annuelle des prix/);
  });

  it('explains the fields of the assumptions dialog', async () => {
    serve();
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getAllByRole('button', { name: /^À propos : / })).toHaveLength(5 + 6); // the five rate legends, the six other assumptions
  });

  it('shows the observed series', async () => {
    serve();
    await renderApp('/settings/projection');

    expect(await screen.findByText('Sur 12 mois à fin septembre 2026 : 1,2 %')).toBeInTheDocument();
    const row = screen.getByRole('row', { name: /2025/ });
    expect(within(row).getByText('0,9 %')).toBeInTheDocument();
    expect(screen.getByText(/Dernier mois publié : septembre 2026/)).toBeInTheDocument();
  });

  it('opens the projected and the observed inflation', async () => {
    serve();
    const { user, router } = await renderApp('/settings/projection');

    const links = await screen.findAllByRole('link', { name: 'Voir la série' });
    await user.click(links[0]!);
    await waitFor(() => expect(router.state.location.pathname).toBe('/series'));
    expect(router.state.location.search).toMatchObject({ s: 'projection:inflation' });

    await router.navigate({ to: '/settings/projection' });
    await user.click((await screen.findAllByRole('link', { name: 'Voir la série' }))[1]!);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ s: 'cpi:yoy,projection:inflation' }));
  });

  it('shows unavailable when the series is empty', async () => {
    serve(assumptions(), { lastPublished: null, last12Months: null, years: [] });
    await renderApp('/settings/projection');

    expect(await screen.findByText('Inflation observée indisponible.')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Inflation observée' })).queryByRole('table')).not.toBeInTheDocument();
  });

  it('hides the 12-month line when only the yearly series is known', async () => {
    serve(assumptions(), { ...observed, last12Months: null });
    await renderApp('/settings/projection');

    expect(await screen.findByRole('row', { name: /2025/ })).toBeInTheDocument();
    expect(screen.getByText(/Dernier mois publié : septembre 2026/)).toBeInTheDocument();
    expect(screen.queryByText(/Sur 12 mois/)).not.toBeInTheDocument();
  });
});
