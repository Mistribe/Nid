import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { ScenarioDefaults, ScenarioResult } from '../api';

const ALICE = '01926b9e-0000-7000-8000-00000000000a';
const LIVRET = '0192f0aa-0000-7000-8000-0000000000c1';
const PER = '0192f0aa-0000-7000-8000-0000000000c2';
const CREATED = '0192f0aa-0000-7000-8000-0000000000d1';
const eur = (amount: string) => ({ amount, currency: 'EUR' });

const defaults: ScenarioDefaults = {
  classRates: { cash: '2', money_market: '2.5', bond: '3', equity: '6', mixed: '4', capital_guaranteed: '2.5', real_estate: '2', other: '0' },
  monthlyIncome: eur('5200.00'), commitments: eur('900.00'),
  holdings: [
    { id: LIVRET, name: 'Livret A', value: eur('22950.00'), liquidity: 'available' },
    { id: PER, name: 'PER Alice', value: eur('41000.00'), liquidity: 'locked' },
  ],
};

const result: ScenarioResult = {
  capacity: {
    retainedMonth: '2027-04', newPayment: eur('1571.43'), loansCost: eur('161429.00'), lastPayment: '2052-03-01',
    debtRatioBefore: 12.5, debtRatioAfter: 32.7, leftBefore: eur('3100.00'), leftAfter: eur('2328.57'),
    monthlyTaxIncrease: eur('0.00'), savingsAfterByLiquidity: { available: eur('2950.00') },
  },
  points: [], withoutAtHorizon: eur('180000.00'), withAtHorizon: eur('210000.00'), gap: eur('30000.00'), loans: [],
  warnings: [{ code: 'funding_gap', value: '-6000.00' }],
};

function serve() {
  const simulated: { document: Record<string, unknown> }[] = [];
  const created: unknown[] = [];
  server.use(
    http.get('*/api/v1/scenarios/defaults', () => HttpResponse.json(defaults)),
    http.post('*/api/v1/scenarios/simulate', async ({ request }) => {
      simulated.push(await request.json() as { document: Record<string, unknown> });
      return HttpResponse.json(result);
    }),
    http.post('*/api/v1/scenarios', async ({ request }) => { created.push(await request.json()); return HttpResponse.json({ id: CREATED }, { status: 201 }); }),
  );
  return { simulated, created };
}

type User = Awaited<ReturnType<typeof renderApp>>['user'];
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Continuer' }));
const step = () => screen.getByRole('listitem', { current: 'step' });

async function property(user: User) {
  await user.type(await screen.findByLabelText('Nom du scénario'), 'T3 Nantes');
  await user.type(screen.getByLabelText('Date d’achat'), '01/03/2027');
  await user.type(screen.getByLabelText('Prix'), '320000');
  await user.clear(screen.getByLabelText('Frais de notaire'));
  await user.type(screen.getByLabelText('Frais de notaire'), '24000');
  await user.clear(screen.getByLabelText('Loyer actuel (mensuel)'));
  await user.type(screen.getByLabelText('Loyer actuel (mensuel)'), '950');
  await next(user);
}

async function loan(user: User) {
  await user.type(screen.getByLabelText('Montant emprunté'), '310000');
  await user.type(screen.getByLabelText('Taux (%)'), '3,6');
  await user.type(screen.getByLabelText('Durée (mois)'), '300');
}

describe('NewScenarioPage', () => {
  beforeAll(async () => { await import('./NewScenarioPage'); }, 30_000);

  it('goes through the four steps, simulates on the way, then creates the scenario and opens it', async () => {
    const { simulated, created } = serve();
    const { user, router } = await renderApp('/scenarios/new');

    await property(user);
    expect(step()).toHaveTextContent('Financement');
    await user.click(screen.getByRole('button', { name: 'Ajouter un apport' }));
    await pick(user, 'Enveloppe', /Livret A/);
    await user.type(screen.getByLabelText('Montant de l’apport'), '20000');
    await next(user);

    expect(step()).toHaveTextContent('Prêts');
    await loan(user);
    await next(user);

    expect(step()).toHaveTextContent('Hypothèses');
    expect(screen.getByLabelText('Horizon (années)')).toHaveValue('10');
    await user.clear(screen.getByLabelText('Épargne mensuelle actuelle'));
    await user.type(screen.getByLabelText('Épargne mensuelle actuelle'), '800');
    await pick(user, 'Classe de l’épargne', 'Monétaire');

    await waitFor(() => {
      const last = simulated.at(-1)?.document as { price?: string; loans?: { rate: string }[] } | undefined;
      expect(last?.price).toBe('320000');
      expect(last?.loans?.[0]?.rate).toBe('3.6');
    });
    const summary = screen.getByRole('complementary', { name: 'Résumé' });
    expect(await within(summary).findByText('1 571,43 €')).toBeInTheDocument();
    expect(within(summary).getByText(/écart de -6 000,00 €/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Créer le scénario' }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/scenarios/${CREATED}`));
    expect(created).toEqual([{
      name: 'T3 Nantes', visibility: 'shared', scope: 'household', owners: [{ personId: ALICE, shareBp: 10000 }],
      document: {
        version: 1, use: 'primary', purchaseOn: '2027-03-01', price: '320000', notaryFees: '24000', agencyFees: '0', otherCosts: '0',
        monthlyCharges: '0', currentRent: '950', expectedRent: '0', vacancyMonths: 0, rentalRegime: null,
        funding: [{ item: LIVRET, amount: '20000' }],
        loans: [{
          name: 'Prêt 1', amount: '310000', rate: '3.6', months: 300, deferral: null,
          insurance: { kind: 'rate_on_initial', value: '0' }, fileFees: '0', guaranteeFees: '0',
        }],
        assumptions: {
          horizonYears: 10, classRates: defaults.classRates, propertyGrowth: '2', monthlySavings: '800', savingsClass: 'money_market',
        },
      },
    }]);
  });

  it('lists a locked envelope with its reason, but it cannot be picked', async () => {
    serve();
    const { user } = await renderApp('/scenarios/new');

    await property(user);
    await user.click(screen.getByRole('button', { name: 'Ajouter un apport' }));
    await user.click(screen.getByRole('combobox', { name: 'Enveloppe' }));
    const locked = await within(await screen.findByRole('listbox')).findByRole('option', { name: /PER Alice.*bloquée/ });
    expect(locked).toHaveAttribute('aria-disabled', 'true');
    await user.click(locked);
    expect(screen.getByRole('combobox', { name: 'Enveloppe' })).not.toHaveTextContent('PER Alice');
  });

  it('shows a refused field on its own step, back from the last one', async () => {
    const { created } = serve();
    server.use(http.post('*/api/v1/scenarios', async ({ request }) => {
      created.push(await request.json());
      return HttpResponse.json({
        type: 'about:blank', title: 'Invalid', status: 422,
        errors: [{ path: 'loans[0].rate', code: 'request.rate', message: 'A rate is a percent from 0 to 20.' }],
      }, { status: 422, headers: { 'Content-Type': 'application/problem+json' } });
    }));
    const { user } = await renderApp('/scenarios/new');

    await property(user);
    await next(user);
    await loan(user);
    await next(user);
    await user.click(screen.getByRole('button', { name: 'Créer le scénario' }));

    await waitFor(() => expect(step()).toHaveTextContent('Prêts'));
    expect(screen.getByLabelText('Taux (%)')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByText('Un taux en %, de 0 à 20.')).toBeInTheDocument();
  });
});
