import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { LoanContractView, LoanDetail, LoanInsuranceComparison, LoanRenegotiation } from '../api';
import { LOAN, loanDetail, schedule } from '../testData';

type Contract = components['schemas']['LoanContractBody'];
const PAGE = `/loans/${LOAN}?tab=operations`;
const PHASE = { rateType: 'fixed', paymentOverride: null, openingBalanceOverride: null, note: null } as const;

const prepared = (over: Partial<LoanContractView> = {}): LoanContractView => ({
  ...loanDetail().contract,
  phases: [
    { seq: 1, mode: 'annuity', annualRate: '0.035', payments: 12, ...PHASE },
    { seq: 2, mode: 'annuity', annualRate: '0.02', payments: null, ...PHASE },
  ],
  ...over,
});

const renegotiation = (over: Partial<LoanRenegotiation> = {}): LoanRenegotiation => ({
  on: '2026-10-12', annualRate: '0.02', payments: 228, fees: '2000.00',
  current: { payment: '1159.92', endDate: '2045-10-15', paymentsLeft: 228, interestLeft: '70057.31', insuranceLeft: '0.00', fees: '0.00', totalCost: '70057.31' },
  renegotiated: { payment: '1003.11', endDate: '2045-10-15', paymentsLeft: 228, interestLeft: '38567.20', insuranceLeft: '0.00', fees: '2000.00', totalCost: '40567.20' },
  saving: '29490.11', breakEvenOn: '2027-09-15', contract: prepared(), ...over,
});

function serve(detail: LoanDetail, answers: { renegotiation?: LoanRenegotiation; insurance?: LoanInsuranceComparison } = {}) {
  const asked: unknown[] = [];
  const saved: Contract[] = [];
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(detail)),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
    http.post('*/api/v1/realestate/loans/:id/simulations/renegotiation', async ({ request }) => {
      asked.push(await request.json());
      return HttpResponse.json(answers.renegotiation ?? renegotiation());
    }),
    http.post('*/api/v1/realestate/loans/:id/simulations/insurance', async ({ request }) => {
      asked.push(await request.json());
      return HttpResponse.json(answers.insurance);
    }),
    http.put('*/api/v1/realestate/loans/:id/contract', async ({ request }) => {
      saved.push((await request.json()) as Contract);
      return HttpResponse.json({ id: LOAN });
    }),
  );
  return { asked, saved };
}

const region = (name: string) => screen.findByRole('region', { name });
const line = (table: HTMLElement, name: string) => within(table).getByRole('rowheader', { name }).closest('tr')!;

describe('the renegotiation and the insurance quote', () => {
  beforeAll(async () => { await import('../LoanDetailPage'); await import('../contract/ContractPage'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 9, 12, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('compares the loan with its renegotiation side by side, says when it pays for itself, and prepares the contract', async () => {
    const { asked, saved } = serve(loanDetail());
    const { user, router, container } = await renderApp(PAGE);

    const section = await region('Simuler une renégociation');
    await user.type(within(section).getByLabelText('Nouveau taux annuel (%)'), '2');
    await user.type(within(section).getByLabelText('Frais de la banque'), '1500');
    await user.type(within(section).getByLabelText('Indemnités de remboursement anticipé'), '500');
    await user.click(within(section).getByRole('button', { name: 'Simuler' }));

    const table = await within(section).findByRole('table', { name: 'Crédit actuel et crédit renégocié' });
    expect(line(table, 'Première mensualité après la date')).toHaveTextContent('1 159,92 €1 003,11 €');
    expect(line(table, 'Coût total, frais compris')).toHaveTextContent('70 057,31 €40 567,20 €');
    expect(section).toHaveTextContent('Économie nette, frais déduits : +29 490,11 €');
    expect(section).toHaveTextContent('Rentable à partir de l’échéance du 15 septembre 2027.');
    expect(asked).toEqual([{ on: '2026-10-12', annualRate: '0.02', bankFees: '1500', penalty: '500' }]);
    await expectNoAxeViolations(container);

    // « Enregistrer » opens the contract page on the prepared contract: nothing is sent before « Enregistrer le contrat ».
    await user.click(within(section).getByRole('button', { name: 'Enregistrer cette renégociation' }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/loans/${LOAN}/contract`));
    expect(await screen.findByText('Contrat préparé par la simulation : vérifiez-le, puis enregistrez-le. Rien n’est envoyé avant.')).toBeInTheDocument();
    expect(within(screen.getByRole('group', { name: 'Phase 2' })).getByLabelText('Taux annuel (%)')).toHaveValue('2');
    expect(saved).toEqual([]);
    await user.click(screen.getByRole('button', { name: 'Enregistrer le contrat' }));
    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]).toMatchObject({ version: 3, phases: [
      { seq: 1, mode: 'annuity', annualRate: '0.035', rateType: 'fixed', payments: 12 },
      { seq: 2, mode: 'annuity', annualRate: '0.02', rateType: 'fixed' },
    ] });
  });

  it('says « jamais rentable » when the fees exceed the saving, and offers no record to a reader', async () => {
    serve(loanDetail({ canWrite: false }), { renegotiation: renegotiation({ saving: '-1200.00', breakEvenOn: null }) });
    const { user } = await renderApp(PAGE);

    const section = await region('Simuler une renégociation');
    await user.type(within(section).getByLabelText('Nouveau taux annuel (%)'), '3,3');
    await user.click(within(section).getByRole('button', { name: 'Simuler' }));

    expect(await within(section).findByText('Jamais rentable : les frais dépassent l’économie.')).toBeInTheDocument();
    expect(section).toHaveTextContent('Économie nette, frais déduits : -1 200,00 €');
    expect(within(section).queryByRole('button', { name: 'Enregistrer cette renégociation' })).not.toBeInTheDocument();
  });

  it('compares a quote per insured person and prepares the insurance change', async () => {
    const alice = { mode: 'rate_on_initial', monthlyAmount: null, annualRate: '0.003', startsOn: '2025-11-15', endsOn: null, note: 'Alice' };
    const bob = { mode: 'fixed_monthly', monthlyAmount: '20.00', annualRate: null, startsOn: '2025-11-15', endsOn: null, note: 'Bob' };
    const quote = { mode: 'rate_on_remaining', monthlyAmount: null, annualRate: '0.001', startsOn: '2026-10-12', endsOn: null, note: null };
    const detail = loanDetail({ contract: { ...loanDetail().contract, insurances: [alice, bob] } });
    const { asked, saved } = serve(detail, {
      insurance: {
        on: '2026-10-12', currentLeft: '15960.00', quotedLeft: '13200.40', saving: '2759.60',
        lines: [
          { insurance: 0, current: alice, currentLeft: '11400.00', quote: null, quoteLeft: null, saving: null },
          { insurance: 1, current: bob, currentLeft: '4560.00', quote, quoteLeft: '1800.40', saving: '2759.60' },
        ],
        contract: { ...detail.contract, insurances: [alice, { ...bob, endsOn: '2026-10-12' }, quote] },
      },
    });
    const { user, router } = await renderApp(PAGE);

    const section = await region('Comparer l’assurance avec un devis');
    await user.click(within(section).getByLabelText('Assurance remplacée'));
    await user.click(await screen.findByRole('option', { name: 'Assurance 2 · Bob' }));
    await user.type(within(section).getByLabelText('Taux annuel (%)'), '0,1');
    await user.click(within(section).getByRole('button', { name: 'Comparer' }));

    const table = await within(section).findByRole('table', { name: 'Assurance restant à payer par assuré' });
    expect(line(table, 'Assurance 1 · Alice')).toHaveTextContent('11 400,00 €');
    expect(line(table, 'Assurance 2 · Bob')).toHaveTextContent('4 560,00 €1 800,40 €+2 759,60 €');
    expect(line(table, 'Total')).toHaveTextContent('15 960,00 €13 200,40 €+2 759,60 €');
    expect(asked).toEqual([{ on: '2026-10-12', quotes: [{ insurance: 1, mode: 'rate_on_remaining', annualRate: '0.001' }] }]);

    await user.click(within(section).getByRole('button', { name: 'Enregistrer ce changement d’assurance' }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/loans/${LOAN}/contract`));
    expect(await screen.findByRole('group', { name: 'Assurance 3' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Enregistrer le contrat' }));
    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0]!.insurances).toEqual([
      { mode: 'rate_on_initial', annualRate: '0.003', startsOn: '2025-11-15', note: 'Alice' },
      { mode: 'fixed_monthly', monthlyAmount: '20.00', startsOn: '2025-11-15', endsOn: '2026-10-12', note: 'Bob' },
      { mode: 'rate_on_remaining', annualRate: '0.001', startsOn: '2026-10-12' },
    ]);
  });
});
