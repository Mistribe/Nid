import { act, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectHeaderActionRule } from '@/test/headerActions';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import type { LoanDetail } from '../api';
import { LOAN, loanDetail, schedule } from '../testData';

type Body = components['schemas']['LoanContractBody'];
type User = Awaited<ReturnType<typeof renderApp>>['user'];
const PAGE = `/loans/${LOAN}/contract`;

/** The detail as the server answers it; the contract replaced against its version, as the server does. */
function serve(initial: LoanDetail = loanDetail(), refuse?: () => Response) {
  let current = initial;
  const bodies: Body[] = [];
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
    http.put('*/api/v1/realestate/loans/:id/contract', async ({ request }) => {
      const body = (await request.json()) as Body;
      bodies.push(body);
      if (refuse) return refuse();
      if (body.version !== current.contract.version)
        return HttpResponse.json({ status: 409, code: 'realestate.loan_conflict', title: 'Changed.' }, { status: 409 });
      current = { ...current, contract: { ...current.contract, version: current.contract.version + 1 } };
      return HttpResponse.json({ id: current.id });
    }),
  );
  return { bodies, change: (next: LoanDetail) => { current = next; } };
}

const refusal = (path: string, code = 'realestate.invalid_loan') => () =>
  HttpResponse.json({ status: 422, code, title: 'x', errors: [{ path, code, message: 'x' }] }, { status: 422 });
const form = () => screen.findByRole('form', { name: 'Contrat du crédit' });
const group = (name: string) => screen.getByRole('group', { name });
const save = (user: User) => user.click(screen.getByRole('button', { name: 'Enregistrer le contrat' }));

const PHASED = loanDetail({
  contract: {
    ...loanDetail().contract,
    phases: [
      { seq: 1, mode: 'deferred_partial', annualRate: '0.035', rateType: 'fixed', payments: 24, paymentOverride: null, openingBalanceOverride: null, note: 'travaux' },
      { seq: 2, mode: 'annuity', annualRate: '0.0412', rateType: 'variable', payments: null, paymentOverride: '1250.00', openingBalanceOverride: null, note: null },
    ],
    insurances: [{ mode: 'rate_on_initial', monthlyAmount: null, annualRate: '0.0036', startsOn: '2025-11-15', endsOn: null, note: 'Alice' }],
  },
});

describe('ContractPage', () => {
  // The lazy route chunks loaded cold inside the first test race its timeouts under load.
  beforeAll(async () => { await import('./ContractPage'); await import('../LoanDetailPage'); }, 30_000);

  it('opens a plain contract as a plain loan, rates back in %, sends it whole with its version and lands on the detail', async () => {
    const { bodies } = serve();
    const { user, router, container } = await renderApp(PAGE);

    await form();
    expect(screen.getByRole('heading', { name: 'Modifier le contrat : Prêt maison', level: 1 })).toBeInTheDocument();
    expect(screen.getByLabelText('Capital emprunté')).toHaveValue('200000,00');
    expect(screen.getByLabelText('Taux annuel (%)')).toHaveValue('3,5');
    expect(screen.getByLabelText('Durée (mois)')).toHaveValue('240');
    expect(screen.getByLabelText('Date de déblocage')).toHaveValue('15/10/2025');
    expect(screen.getByLabelText('Première échéance')).toHaveValue('15/11/2025');
    expect(screen.queryByRole('group', { name: /^Phase/ })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Nom')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.clear(screen.getByLabelText('Taux annuel (%)'));
    await user.type(screen.getByLabelText('Taux annuel (%)'), '3,2');
    // The stored first payment is the bank's: a corrected disbursement does not move it.
    await user.clear(screen.getByLabelText('Date de déblocage'));
    await user.type(screen.getByLabelText('Date de déblocage'), '14/10/2025');
    expect(screen.getByLabelText('Première échéance')).toHaveValue('15/11/2025');
    await save(user);

    await waitFor(() => expect(router.state.location.pathname).toBe(`/loans/${LOAN}`));
    expect(bodies).toEqual([{
      version: 3, principal: '200000.00', totalPayments: 240, disbursedOn: '2025-10-14', firstPaymentDate: '2025-11-15',
      rounding: 'half_up', firstPeriod: 'full', phases: [{ seq: 1, mode: 'annuity', annualRate: '0.032', rateType: 'fixed' }], insurances: [],
    }]);
    await waitFor(() => expect(toasts()).toHaveTextContent('Contrat enregistré.'));
  });

  it('opens any other contract on its phase list; a stored insurance keeps its start, an added one follows the first payment', async () => {
    const { bodies } = serve(PHASED);
    const { user, container } = await renderApp(PAGE);

    await form();
    expect(within(group('Phase 1')).getByLabelText('Type d’échéance')).toHaveTextContent('Différé partiel (intérêts seuls)');
    expect(within(group('Phase 1')).getByLabelText('Nombre d’échéances')).toHaveValue('24');
    expect(within(group('Phase 2')).getByLabelText('Taux annuel (%)')).toHaveValue('4,12');
    expect(within(group('Phase 2')).getByLabelText('Nature du taux')).toHaveTextContent('Variable');
    const stored = group('Assurance 1');
    expect(within(stored).getByLabelText('Taux annuel (%)')).toHaveValue('0,36');
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Ajouter une assurance' }));
    const added = group('Assurance 2');
    expect(within(added).getByLabelText('Début')).toHaveValue('15/11/2025');
    await user.clear(screen.getByLabelText('Première échéance'));
    await user.type(screen.getByLabelText('Première échéance'), '01/12/2025');
    expect(within(stored).getByLabelText('Début')).toHaveValue('15/11/2025');
    expect(within(added).getByLabelText('Début')).toHaveValue('01/12/2025');
    await user.type(within(added).getByLabelText('Montant par mois'), '12');
    await save(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ version: 3, firstPaymentDate: '2025-12-01' });
    expect(bodies[0]!.phases).toEqual([
      { seq: 1, mode: 'deferred_partial', annualRate: '0.035', rateType: 'fixed', payments: 24, note: 'travaux' },
      { seq: 2, mode: 'annuity', annualRate: '0.0412', rateType: 'variable', paymentOverride: '1250.00' },
    ]);
    expect(bodies[0]!.insurances).toEqual([
      { mode: 'rate_on_initial', annualRate: '0.0036', startsOn: '2025-11-15', note: 'Alice' },
      { mode: 'fixed_monthly', monthlyAmount: '12', startsOn: '2025-12-01' },
    ]);
  });

  it('shows a refusal under the phase it names', async () => {
    serve(PHASED, refusal('phases[1].annualRate'));
    const { user } = await renderApp(PAGE);

    await form();
    await save(user);

    expect(await within(group('Phase 2')).findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('names at the top a stored repayment the new contract makes impossible', async () => {
    const repayments = [
      { id: 'r1', onDate: '2026-03-20', amount: '10000.00', effect: 'reduce_payment', penalty: null, note: null, version: 1 },
      { id: 'r2', onDate: '2027-01-15', amount: '150000.00', effect: 'reduce_term', penalty: null, note: null, version: 1 },
    ];
    serve(loanDetail({ repayments }), refusal('repayments[1].amount', 'realestate.event_above_balance'));
    const { user } = await renderApp(PAGE);

    await form();
    await user.clear(screen.getByLabelText('Capital emprunté'));
    await user.type(screen.getByLabelText('Capital emprunté'), '100000');
    await save(user);

    expect(await screen.findByRole('alert')).toHaveTextContent('Ce contrat rend impossible le remboursement du 15 janvier 2027 (150 000,00 €).');
  });

  it('says the contract changed meanwhile, keeps what is typed through a refetch, and « Recharger » resets the form', async () => {
    const { bodies, change } = serve();
    const { user, queryClient } = await renderApp(PAGE);

    await form();
    await user.clear(screen.getByLabelText('Taux annuel (%)'));
    await user.type(screen.getByLabelText('Taux annuel (%)'), '3,2');
    change(loanDetail({ contract: { ...loanDetail().contract, principal: '180000.00', version: 4 } }));
    await act(() => queryClient.invalidateQueries());
    expect(screen.getByLabelText('Taux annuel (%)')).toHaveValue('3,2');
    expect(screen.getByLabelText('Capital emprunté')).toHaveValue('200000,00');
    await save(user);

    expect(await screen.findByRole('alert')).toHaveTextContent('Ce crédit a changé entre-temps : rechargez la page.');
    expect(screen.getByText('« Recharger » remplace le formulaire par le contrat enregistré : vos modifications non enregistrées sont perdues.')).toBeInTheDocument();
    expect(bodies[0]).toMatchObject({ version: 3 });

    await user.click(screen.getByRole('button', { name: 'Recharger' }));
    await waitFor(() => expect(screen.getByLabelText('Capital emprunté')).toHaveValue('180000,00'));
    expect(screen.getByLabelText('Taux annuel (%)')).toHaveValue('3,5');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('is reached from the detail’s « Modifier le contrat »', async () => {
    serve();
    const { user } = await renderApp(`/loans/${LOAN}?tab=contract`);

    await user.click(await screen.findByRole('link', { name: 'Modifier le contrat' }));

    expect(await form()).toBeInTheDocument();
  });

  it('tells a reader without write access the contract is read-only, without a form', async () => {
    serve(loanDetail({ canWrite: false }));
    const { container } = await renderApp(PAGE);

    expect(await screen.findByText('Vous pouvez consulter ce crédit, pas le modifier.')).toBeInTheDocument();
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('is a form page: a left-aligned header with a crumb back to the loan, no card around the form', async () => {
    serve();
    const { container } = await renderApp(PAGE);

    const formEl = await form();
    const header = container.querySelector('header[data-page-header]')!;
    expect(within(header as HTMLElement).queryByRole('navigation', { name: 'Fil d’Ariane' })).toBeNull();
    expect(within(header as HTMLElement).getByRole('heading', { level: 1 })).toHaveTextContent('Modifier le contrat : Prêt maison');
    expect(within(within(document.querySelector<HTMLElement>('[data-pane="toolbar"]')!).getByRole('navigation', { name: 'Fil d’Ariane' })).getByRole('link', { name: 'Prêt maison' })).toHaveAttribute('href', `/loans/${LOAN}`);
    expectHeaderActionRule(container);
    expect(formEl.closest('.bg-card')).toBeNull();
  });

  it('says an unknown or unreadable loan is not found, under the loans crumb alone', async () => {
    server.use(http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(PAGE);

    expect(await screen.findByRole('heading', { level: 1, name: 'Page introuvable' })).toBeInTheDocument();
    // The error page keeps the page's trail: « Crédits » alone, never the error sentence.
    const trail = within(within(document.querySelector<HTMLElement>('[data-pane="toolbar"]')!).getByRole('navigation', { name: 'Fil d’Ariane' }));
    expect(trail.getByRole('link', { name: 'Crédits' })).toHaveAttribute('href', '/loans');
    expect(trail.getAllByRole('link')).toHaveLength(1);
  });
});
