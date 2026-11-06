import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { delay } from 'msw';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { expectTrailBack } from '@/test/trail';
import { server } from '@/test/server';
import type { Scenario, ScenarioDefaults } from './api';

const ID = '0192f0aa-0000-7000-8000-0000000000e1';
const LIVRET = '0192f0aa-0000-7000-8000-0000000000c1';
const eur = (amount: string) => ({ amount, currency: 'EUR' });
const RATES = { cash: '2', money_market: '2.5', bond: '3', equity: '6', mixed: '4', capital_guaranteed: '2.5', real_estate: '2', other: '0' };

const view: Scenario = {
  id: ID, name: 'T3 Nantes', visibility: 'shared', scope: 'household', canWrite: true,
  document: {
    version: 1, use: 'primary', purchaseOn: '2027-03-01', price: '320000.00', notaryFees: '24000.00', agencyFees: '0.00', otherCosts: '0.00',
    monthlyCharges: '150.00', currentRent: '950.00', expectedRent: '0.00', vacancyMonths: 0, rentalRegime: null,
    funding: [{ item: LIVRET, amount: '20000.00' }],
    loans: [{
      name: 'Prêt principal', amount: '310000.00', rate: '3.6', months: 300, deferral: null,
      insurance: { kind: 'rate_on_initial', value: '0.3' }, fileFees: '0.00', guaranteeFees: '0.00',
    }],
    assumptions: { horizonYears: 10, classRates: RATES, propertyGrowth: '2', monthlySavings: '800.00', savingsClass: 'cash' },
  },
  result: {
    capacity: {
      retainedMonth: '2027-04', newPayment: eur('1571.43'), loansCost: eur('161429.00'), lastPayment: '2052-03-01',
      debtRatioBefore: 12.5, debtRatioAfter: 32.7, leftBefore: eur('3100.00'), leftAfter: eur('2328.57'),
      monthlyTaxIncrease: eur('0.00'), savingsAfterByLiquidity: { available: eur('2950.00'), locked: eur('41000.00') },
    },
    points: [
      { on: '2026-10-23', without: eur('100000.00'), with: eur('100000.00') },
      { on: '2026-11-30', without: eur('100500.00'), with: eur('100400.00') },
      { on: '2026-12-31', without: eur('101000.00'), with: eur('100800.00') },
      { on: '2036-10-31', without: eur('180000.00'), with: eur('210000.00') },
    ],
    withoutAtHorizon: eur('180000.00'), withAtHorizon: eur('210000.00'), gap: eur('30000.00'),
    loans: [{
      name: 'Prêt principal', interest: eur('150000.00'), insurance: eur('11429.00'), fees: eur('0.00'), lastPayment: '2052-03-01',
      schedule: {
        rows: [{ number: 1, dueDate: '2027-04-01', payment: '1571.43', interest: '930.00', principal: '641.43', insurance: '77.50', earlyRepayment: null, balance: '309358.57' }],
        years: [{ year: 2027, payment: '14142.87', interest: '8340.00', principal: '5802.87', insurance: '697.50', earlyRepayment: '0.00', balance: '304197.13' }],
        totalInterest: '150000.00', totalPrincipal: '310000.00',
      },
    }],
    warnings: [{ code: 'savings_exhausted', value: '2028-03' }],
  },
};

const defaults: ScenarioDefaults = {
  classRates: RATES, monthlyIncome: eur('5200.00'), commitments: eur('900.00'),
  holdings: [{ id: LIVRET, name: 'Livret A', value: eur('22950.00'), liquidity: 'available' }],
};

function serve(first: Scenario = view) {
  let current = first;
  const put: { document: Scenario['document'] }[] = [];
  server.use(
    http.get('*/api/v1/scenarios/defaults', () => HttpResponse.json(defaults)),
    http.get(`*/api/v1/scenarios/${ID}`, () => HttpResponse.json(current)),
    http.put(`*/api/v1/scenarios/${ID}`, async ({ request }) => {
      const body = await request.json() as { document: Scenario['document'] };
      put.push(body);
      current = { ...current, document: body.document, result: { ...current.result, gap: eur('27500.00') } };
      return HttpResponse.json(current);
    }),
  );
  return { put };
}

const figures = () => screen.getByRole('region', { name: 'Chiffres clés' });

describe('ScenarioDetailPage', () => {
  beforeAll(async () => { await import('./ScenarioDetailPage'); }, 30_000);

  it('loading and error states show no inline back link: the bar leads back to the list', async () => {
    server.use(http.get(`*/api/v1/scenarios/${ID}`, async () => { await delay('infinite'); return HttpResponse.json({}); }));
    await renderApp(`/scenarios/${ID}`);
    expect(await screen.findByLabelText('Chargement du scénario')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Simulations', '/scenarios'));
    cleanup();

    server.use(http.get(`*/api/v1/scenarios/${ID}`, () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/scenarios/${ID}`);
    expect(await screen.findByRole('heading', { level: 1, name: 'Page introuvable' })).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Simulations', '/scenarios'));
  });

  it('shows the server figures in the stat line and the capacity tab by default', async () => {
    serve();
    await renderApp(`/scenarios/${ID}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'T3 Nantes' })).toBeInTheDocument();
    const stats = within(figures());
    expect(stats.getByText('Nouvelle mensualité').nextSibling).toHaveTextContent('1 571,43 €');
    expect(stats.getByText('Endettement après').nextSibling).toHaveTextContent('32,7 %');
    expect(stats.getByText('Reste à vivre après').nextSibling).toHaveTextContent('2 328,57 €');
    expect(stats.getByText('Écart de patrimoine à l’horizon').nextSibling).toHaveTextContent('30 000,00 €');
    expect(screen.getByText('Chiffres indicatifs : rendements saisis, pas d’inflation.')).toBeInTheDocument();

    const before = screen.getByRole('table', { name: 'Avant et après l’achat' });
    expect(within(before).getByRole('row', { name: /Reste à vivre/ })).toHaveTextContent(/3 100,00 €.*2 328,57 €/);
    const savings = screen.getByRole('table', { name: 'Épargne après l’apport' });
    expect(within(savings).getByRole('row', { name: /Disponible tout de suite/ })).toHaveTextContent('2 950,00 €');
    expect(screen.getByText('Épargne disponible épuisée en mars 2028.')).toBeInTheDocument();
  });

  it('shows the year-end points, the loan schedules and the assumptions in their tabs', async () => {
    serve();
    const { user } = await renderApp(`/scenarios/${ID}`);

    await user.click(await screen.findByRole('tab', { name: 'Projection' }));
    const points = await screen.findByRole('table', { name: 'Patrimoine net en fin d’année' });
    expect(within(points).getByRole('columnheader', { name: 'Sans l’achat' })).toBeInTheDocument();
    expect(within(points).getByRole('columnheader', { name: 'Avec l’achat' })).toBeInTheDocument();
    expect(within(points).getByRole('row', { name: /31 décembre 2026/ })).toHaveTextContent(/101 000,00 €.*100 800,00 €/);
    expect(within(points).getByRole('row', { name: /31 octobre 2036/ })).toHaveTextContent(/180 000,00 €.*210 000,00 €/);
    expect(within(points).queryByRole('row', { name: /30 novembre 2026/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Prêts' }));
    const loan = await screen.findByRole('region', { name: 'Prêt principal' });
    expect(within(loan).getByRole('button', { name: /2027/ })).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Hypothèses' }));
    expect(screen.getByText('Horizon (années)').nextSibling).toHaveTextContent('10');
    expect(screen.getByText('Actions (%)').nextSibling).toHaveTextContent('6');
  });

  it('edits a rate in the assumptions dialog, sends the whole document and shows the refetched figures', async () => {
    const { put } = serve();
    const { user } = await renderApp(`/scenarios/${ID}?tab=assumptions`);

    await user.click(await screen.findByRole('button', { name: 'Modifier' }));
    const dialog = await screen.findByRole('dialog', { name: 'Modifier les hypothèses' });
    await user.click(within(dialog).getByRole('button', { name: 'Rendements par classe' }));
    await user.clear(within(dialog).getByLabelText('Actions (%)'));
    await user.type(within(dialog).getByLabelText('Actions (%)'), '5');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(put).toHaveLength(1));
    expect(put[0]).toMatchObject({ name: 'T3 Nantes', visibility: 'shared', scope: 'household' });
    expect(put[0]!.document.assumptions.classRates.equity).toBe('5');
    expect(put[0]!.document.loans[0]!.name).toBe('Prêt principal');
    expect(put[0]!.document.funding).toEqual([{ item: LIVRET, amount: '20000.00' }]);
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(within(figures()).getByText('Écart de patrimoine à l’horizon').nextSibling).toHaveTextContent('27 500,00 €');
  });

  it('reopens the whole form from the header', async () => {
    serve();
    const { user } = await renderApp(`/scenarios/${ID}`);

    await user.click(await screen.findByRole('button', { name: 'Modifier le scénario' }));
    const dialog = await screen.findByRole('dialog', { name: 'Modifier le scénario' });
    expect(within(dialog).getByLabelText('Nom du scénario')).toHaveValue('T3 Nantes');
    expect(within(dialog).getByLabelText('Prix')).toHaveValue('320000,00');
  });

  it('offers no edit action to a reader without write access', async () => {
    serve({ ...view, canWrite: false });
    const { user } = await renderApp(`/scenarios/${ID}?tab=assumptions`);

    expect(await screen.findByText('Horizon (années)')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Modifier' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Modifier le scénario' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Capacité' }));
    expect(screen.queryByRole('button', { name: /Modifier/ })).not.toBeInTheDocument();
  });
});
