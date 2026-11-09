import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

type Body = Record<string, unknown>;

const ASSUMPTIONS = '*/api/v1/core/projection-assumptions';
const ALICE = '11111111-1111-4111-8111-111111111111';
const BOB = '22222222-2222-4222-8222-222222222222';

const assumptions = (over: Body = {}) => ({
  classRates: { cash: '1.5', money_market: '2', bond: '3', equity: '6', mixed: '4', capital_guaranteed: '2.5', real_estate: '2', other: '0' },
  inflation: '2', monthlySavings: null, savingsClass: 'equity', freedPaymentsToSavings: true, currency: 'EUR', inflationByYear: {}, classRatesByYear: {}, incomeGrowth: '0', observedInflation: null, ...over,
});

const observed = {
  lastPublished: '2026-09', last12Months: { to: '2026-09', percent: '1.2' },
  years: [{ year: 2025, percent: '0.9' }, { year: 2024, percent: '2.0' }],
};

const YEAR = new Date().getFullYear();
const NEXT = YEAR + 1;

const persons = [
  { id: ALICE, kind: 'member', displayName: 'Alice', birthDate: null, retirementAge: 64 },
  { id: BOB, kind: 'member', displayName: 'Bob', birthDate: '1984-05-20', retirementAge: 62 },
];

function serve(initial: Body = assumptions(), inflation: Body = observed) {
  const puts: Body[] = [];
  const retirements: { id: string; body: Body }[] = [];
  server.use(
    http.get(ASSUMPTIONS, () => HttpResponse.json(initial)),
    http.put(ASSUMPTIONS, async ({ request }) => { const body = (await request.json()) as Body; puts.push(body); return HttpResponse.json(body); }),
    http.get('*/api/v1/core/inflation', () => HttpResponse.json(inflation)),
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.put('*/api/v1/core/persons/:id/retirement', async ({ request, params }) => {
      const body = (await request.json()) as Body;
      retirements.push({ id: params.id as string, body });
      return HttpResponse.json({ ...persons[0], ...body });
    }),
  );
  return { puts, retirements };
}

beforeAll(async () => { await import('./ProjectionSettingsPage'); }, 30_000);

describe('the projection settings page', () => {
  it('shows the household assumptions and the retirement of each person', async () => {
    serve();
    await renderApp('/settings/projection');

    expect(await screen.findByRole('heading', { level: 1, name: 'Réglages' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Projection' })).toHaveAttribute('aria-current', 'page');
    expect(await screen.findByText('Actions : 6 %')).toBeInTheDocument();
    expect(screen.getByText('Liquidités : 1,5 %')).toBeInTheDocument();
    expect(screen.getByText(/Automatique \(moyenne des 12 derniers mois\)/)).toBeInTheDocument();
    expect(screen.getByText('Mensualités terminées versées à l’épargne : oui')).toBeInTheDocument();
    expect(await screen.findByText('20/05/1984')).toBeInTheDocument();
    expect(screen.getByText('62 ans')).toBeInTheDocument();
    expect(screen.getByText('Non renseignée')).toBeInTheDocument();
  });

  it('saves new rates and sends percent strings', async () => {
    const { puts } = serve();
    const { user, queryClient } = await renderApp('/settings/projection');
    queryClient.setQueryData(['get', '/api/v1/projection'], {});

    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    const equity = await screen.findByLabelText('Actions');
    await user.clear(equity);
    await user.type(equity, '7');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(puts).toHaveLength(1));
    expect((puts[0]!.classRates as Body).equity).toBe('7');
    expect(puts[0]).not.toHaveProperty('currency');
    await waitFor(() => expect(queryClient.getQueryState(['get', '/api/v1/projection'])?.isInvalidated).toBe(true));
    expect(puts[0]).toMatchObject({ inflation: '2', monthlySavings: null, savingsClass: 'equity', freedPaymentsToSavings: true });
  });

  it('shows the fixed savings in the household currency', async () => {
    serve(assumptions({ monthlySavings: '500.00', currency: 'CHF' }));
    const { user } = await renderApp('/settings/projection');

    expect(await screen.findByText(/Épargne mensuelle : 500,00\s*CHF/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Modifier les hypothèses' }));
    expect(await screen.findByLabelText('Épargne mensuelle (CHF)')).toHaveValue('500,00');
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

  it('sets a birth date', async () => {
    const { retirements } = serve();
    const { user } = await renderApp('/settings/projection');

    await user.click(await screen.findByRole('button', { name: 'Modifier la retraite de Alice' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Date de naissance'), '20/05/1984');
    const age = within(dialog).getByLabelText('Âge de départ à la retraite');
    await user.clear(age);
    await user.type(age, '64');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(retirements).toEqual([{ id: ALICE, body: { birthDate: '1984-05-20', retirementAge: 64 } }]));
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

    expect(await screen.findByText(`Inflation : ${YEAR} 2,5 %, ${NEXT} 2,2 %, puis 2 %`)).toBeInTheDocument();
    expect(screen.getByText(`Liquidités : ${YEAR} 3 %, puis 1,5 %`)).toBeInTheDocument();
    expect(screen.getByText('Évolution des revenus : inflation + 0,5 %')).toBeInTheDocument();
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
    await screen.findByText('Actions : 6 %');
    await expectNoAxeViolations(container);
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
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('hides the 12-month line when only the yearly series is known', async () => {
    serve(assumptions(), { ...observed, last12Months: null });
    await renderApp('/settings/projection');

    expect(await screen.findByRole('row', { name: /2025/ })).toBeInTheDocument();
    expect(screen.getByText(/Dernier mois publié : septembre 2026/)).toBeInTheDocument();
    expect(screen.queryByText(/Sur 12 mois/)).not.toBeInTheDocument();
  });
});
