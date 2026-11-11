import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { SERIES_POINTS } from '@/api/series';
import { server } from '@/test/server';

type Body = Record<string, unknown>;

const ASSUMPTIONS = '*/api/v1/core/projection-assumptions';
const RATES = { cash: '2', money_market: '2.5', bond: '3', equity: '6', mixed: '4', capital_guaranteed: '2.5', real_estate: '2', other: '0' };
const VOLATILITY = { cash: '0', money_market: '0', bond: '6', equity: '16', mixed: '10', capital_guaranteed: '0', real_estate: '10', other: '0' };

const assumptions = (over: Body = {}) => ({
  classRates: RATES, inflation: '2', monthlySavings: null, savingsClass: 'cash', freedPaymentsToSavings: true, currency: 'EUR', inflationByYear: {}, classRatesByYear: {},
  incomeGrowth: '0', withdrawalRate: '4', monthlyLivingCost: '0.00', classVolatility: VOLATILITY, profile: 'balanced',
  defaults: { classRates: RATES, classVolatility: VOLATILITY, inflation: '2', incomeGrowth: '0', withdrawalRate: '4' },
  observedInflation: null, ...over,
});

const profiles = {
  currency: 'EUR',
  profiles: [
    { key: 'prudent', classRates: RATES, classVolatility: VOLATILITY, inTenYears: '380000.00' },
    { key: 'balanced', classRates: RATES, classVolatility: VOLATILITY, inTenYears: '412000.00' },
    { key: 'optimistic', classRates: RATES, classVolatility: VOLATILITY, inTenYears: '451000.00' },
  ],
};

const YEAR = new Date().getFullYear();

/** The server: each PATCH is kept and answered by the assumptions with its members on top; `refuse` answers a 422 instead. */
function serve(initial: Body = assumptions(), refuse?: { path: string; message: string }) {
  const patches: Body[] = [];
  server.use(
    http.get(ASSUMPTIONS, () => HttpResponse.json(initial)),
    http.patch(ASSUMPTIONS, async ({ request }) => {
      const body = (await request.json()) as Body;
      patches.push(body);
      return refuse
        ? HttpResponse.json({ status: 422, code: 'projection.invalid_field', title: refuse.message, errors: [{ path: refuse.path, code: 'projection.invalid_field', message: refuse.message }] }, { status: 422 })
        : HttpResponse.json({ ...initial, ...body });
    }),
    http.get(`${ASSUMPTIONS}/profiles`, () => HttpResponse.json(profiles)),
    http.get('*/api/v1/projection', () => HttpResponse.json({ monthlySavings: '450.00', savingsOrigin: 'auto' })),
  );
  return patches;
}

const card = (name: string) => screen.findByRole('region', { name });
const SAVINGS = 'Combien mettez-vous de côté chaque mois ?';
const MARKET = 'Comment voyez-vous les marchés ?';
const LIVING = 'De combien aurez-vous besoin par mois une fois à la retraite ?';
const INCOME = 'Vos revenus suivent-ils les prix ?';

beforeAll(async () => { await import('./ProjectionSettingsPage'); }, 30_000);

describe('the projection settings page', () => {
  it('asks plain questions, the expert values folded under « Personnaliser »', async () => {
    serve();
    await renderApp('/settings/projection');

    expect(await screen.findByRole('heading', { level: 1, name: 'Réglages' })).toBeInTheDocument();
    for (const name of [SAVINGS, MARKET, LIVING, INCOME, 'Et les prix ?', 'Départ à la retraite']) expect(await card(name)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Personnaliser' })).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByLabelText('Rendement Actions')).not.toBeInTheDocument();
    expect(screen.queryAllByRole('button', { name: /^À propos : / })).toHaveLength(0);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('shows the automatic savings and saves a fixed amount alone', async () => {
    const patches = serve();
    const { user, queryClient } = await renderApp('/settings/projection');
    queryClient.setQueryData(['get', SERIES_POINTS], {});
    const savings = await card(SAVINGS);

    expect(await within(savings).findByText(/Nid compte environ 450\s€ par mois/)).toBeInTheDocument();
    await user.click(within(savings).getByRole('radio', { name: 'Un montant' }));
    await user.type(within(savings).getByLabelText('Montant par mois (EUR)'), '500');
    await user.click(within(savings).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(patches).toEqual([{ monthlySavings: '500' }]));
    expect(await screen.findByText('Hypothèses enregistrées.')).toBeInTheDocument();
    expect(queryClient.getQueryState(['get', SERIES_POINTS])?.isInvalidated).toBe(true);
  });

  it('goes back to the automatic savings', async () => {
    const patches = serve(assumptions({ monthlySavings: '500.00' }));
    const { user } = await renderApp('/settings/projection');
    const savings = await card(SAVINGS);

    expect(within(savings).getByLabelText('Montant par mois (EUR)')).toHaveValue('500,00');
    await user.click(within(savings).getByRole('radio', { name: 'Laisser Nid calculer' }));

    await waitFor(() => expect(patches).toEqual([{ monthlySavings: 'auto' }]));
  });

  it('saves where the savings go and the freed payments, each alone', async () => {
    const patches = serve(assumptions({ savingsClass: 'bond' }));
    const { user } = await renderApp('/settings/projection');
    const savings = await card(SAVINGS);

    expect(within(savings).getByRole('radio', { name: 'Obligations' })).toBeChecked(); // another stored class shows by its name
    await user.click(within(savings).getByRole('radio', { name: 'En bourse' }));
    await user.click(within(savings).getByRole('switch', { name: 'Quand un crédit se termine, sa mensualité part en épargne' }));

    await waitFor(() => expect(patches).toEqual([{ savingsClass: 'equity' }, { freedPaymentsToSavings: false }]));
  });

  it('offers the market profiles with their effect, from the server', async () => {
    const patches = serve();
    const { user } = await renderApp('/settings/projection');
    const market = await card(MARKET);

    expect(within(market).getByRole('radio', { name: /^Équilibré/ })).toBeChecked();
    expect(await within(market).findByText(/dans 10 ans : ≈ 451\s000\s€/)).toBeInTheDocument();
    await user.click(within(market).getByRole('radio', { name: /^Optimiste/ }));

    await waitFor(() => expect(patches).toEqual([{ profile: 'optimistic' }]));
  });

  it('shows a custom profile, no choice checked', async () => {
    serve(assumptions({ profile: 'custom' }));
    await renderApp('/settings/projection');
    const market = await card(MARKET);

    expect(within(market).getByText('Personnalisé')).toBeInTheDocument();
    for (const radio of within(market).getAllByRole('radio')) expect(radio).not.toBeChecked();
  });

  it('keeps a refusal in the card it belongs to', async () => {
    const patches = serve(assumptions(), { path: 'monthlyLivingCost', message: 'The living cost is not negative.' });
    const { user } = await renderApp('/settings/projection');
    const living = await card(LIVING);

    const field = within(living).getByLabelText('Montant par mois (EUR)');
    await user.clear(field);
    await user.type(field, '-1');
    await user.click(within(living).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(patches).toEqual([{ monthlyLivingCost: '-1' }]));
    expect(await within(living).findByText('The living cost is not negative.')).toBeInTheDocument();
    expect(within(await card(SAVINGS)).queryByText('The living cost is not negative.')).not.toBeInTheDocument();
  });

  it('shows a refusal it cannot place at the top of its card', async () => {
    serve(assumptions(), { path: 'profile', message: 'Not a profile.' });
    const { user } = await renderApp('/settings/projection');
    const market = await card(MARKET);

    await user.click(within(market).getByRole('radio', { name: /^Prudent/ }));

    expect(await within(market).findByRole('alert')).toBeInTheDocument();
  });

  it('saves how incomes follow prices, and names another stored value', async () => {
    const patches = serve(assumptions({ incomeGrowth: '1.5' }));
    const { user } = await renderApp('/settings/projection');
    const income = await card(INCOME);

    expect(within(income).getByRole('radio', { name: 'Personnalisé : 1,5 % au-dessus des prix' })).toBeChecked();
    await user.click(within(income).getByRole('radio', { name: 'Un peu moins' }));

    await waitFor(() => expect(patches).toEqual([{ incomeGrowth: '-1' }]));
  });

  it('gives the prices in one line, one link to the series, and the way to the persons', async () => {
    serve(assumptions({ observedInflation: { to: '2026-09', percent: '3.1' } }));
    const { user, router } = await renderApp('/settings/projection');
    const prices = await card('Et les prix ?');

    expect(await within(prices).findByText(/la cible de la BCE\)\. Sur les 12 derniers mois : 3,1 %\./)).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Voir la série' })).toHaveLength(1);
    expect(within(await card('Départ à la retraite')).getByRole('link', { name: 'Régler les départs' })).toHaveAttribute('href', '/settings/persons');

    await user.click(within(prices).getByRole('link', { name: 'Voir la série' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ s: 'projection:inflation,cpi:yoy' }));
  });

  it('names neither the ECB nor an observed rate for a household outside the euro', async () => {
    serve(assumptions({ currency: 'CHF' }));
    await renderApp('/settings/projection');

    expect(await within(await card('Et les prix ?')).findByText('Les prix augmentent de 2 % par an dans la projection.')).toBeInTheDocument();
  });

  it('shows the stored amount again when going back to automatic is refused', async () => {
    const patches = serve(assumptions({ monthlySavings: '500.00' }), { path: 'monthlySavings', message: 'Refused.' });
    const { user } = await renderApp('/settings/projection');
    const savings = await card(SAVINGS);

    await user.click(within(savings).getByRole('radio', { name: 'Laisser Nid calculer' }));

    await waitFor(() => expect(patches).toHaveLength(1));
    await waitFor(() => expect(within(savings).getByRole('radio', { name: 'Un montant' })).toBeChecked());
    expect(within(savings).getByText('Refused.')).toBeInTheDocument();
  });

  it('offers no reset on rates that are the chosen profile’s', async () => {
    const prudent = { ...RATES, equity: '4', bond: '2' };
    serve(assumptions({ classRates: prudent, profile: 'prudent' }));
    server.use(http.get(`${ASSUMPTIONS}/profiles`, () => HttpResponse.json({ ...profiles, profiles: [{ ...profiles.profiles[0]!, classRates: prudent }, profiles.profiles[1], profiles.profiles[2]] })));
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Personnaliser' }));
    const table = await card('Rendement et volatilité par classe');
    const equity = within(table).getByLabelText('Rendement Actions');
    await user.clear(equity);
    await user.type(equity, '5');

    expect(await within(table).findByRole('button', { name: 'Rendement Actions : revenir à la valeur par défaut, 4 %' })).toBeInTheDocument();
    expect(within(table).getAllByText(/^par défaut/)).toHaveLength(1);
  });

  it('ties a class refusal to its field', async () => {
    serve(assumptions(), { path: 'classRates.equity', message: 'A rate is from -50 % to 50 %.' });
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Personnaliser' }));
    const table = await card('Rendement et volatilité par classe');
    await user.click(within(table).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(within(table).getByLabelText('Rendement Actions')).toHaveAccessibleDescription('A rate is from -50 % to 50 %.'));
    expect(within(table).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows a refusal on a year it does not list at the top of its card', async () => {
    serve(assumptions(), { path: `inflationByYear.${YEAR - 1}`, message: 'A year is from now on.' });
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Personnaliser' }));
    const years = await card('Année par année');
    await user.click(within(years).getByRole('button', { name: 'Enregistrer' }));

    expect(await within(years).findByRole('alert')).toBeInTheDocument();
  });

  it('shows each customised rate with its default, resets it and saves the table', async () => {
    const patches = serve(assumptions({ classRates: { ...RATES, equity: '7' }, profile: 'custom' }));
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Personnaliser' }));
    const table = await card('Rendement et volatilité par classe');
    expect(within(table).getByLabelText('Rendement Actions')).toHaveValue('7');
    expect(within(table).getAllByText(/^par défaut/)).toHaveLength(1);
    await user.click(within(table).getByRole('button', { name: 'Rendement Actions : revenir à la valeur par défaut, 6 %' }));
    expect(within(table).getByLabelText('Rendement Actions')).toHaveValue('6');
    await user.click(within(table).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0]!.classRates).toEqual(RATES);
    expect(patches[0]!.classVolatility).toEqual(VOLATILITY);
  });

  it('saves the years, an emptied class clearing its own', async () => {
    const patches = serve(assumptions({ classRatesByYear: { money_market: { [YEAR]: '3' } } }));
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Personnaliser' }));
    const years = await card('Année par année');
    await user.type(within(years).getByLabelText(`Liquidités ${YEAR}`), '3,2');
    await user.clear(within(years).getByLabelText(`Monétaire ${YEAR}`));
    await user.click(within(years).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(patches).toEqual([{
      inflation: '2', inflationByYear: {}, classRatesByYear: { cash: { [YEAR]: '3.2' }, money_market: {} },
    }]));
  });

  it('explains every expert term under « Personnaliser »', async () => {
    serve();
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Personnaliser' }));
    await card('Taux de retrait');
    expect(screen.getAllByRole('button', { name: /^À propos : / }).map((b) => b.getAttribute('aria-label')))
      .toEqual(['Rendement', 'Volatilité', 'Inflation', 'Liquidités', 'Monétaire', 'Taux de retrait'].map((n) => `À propos : ${n}`));
  });

  it('has no axe violations, folded or open', async () => {
    serve();
    const { container, user } = await renderApp('/settings/projection');
    await card(MARKET);
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Personnaliser' }));
    await card('Taux de retrait');
    await expectNoAxeViolations(container);
  });
});
