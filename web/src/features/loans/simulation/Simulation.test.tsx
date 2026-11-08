import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import type { LoanDetail, LoanSimulation } from '@/api/loans';
import { LOAN, loanDetail, schedule, simulation } from '../testData';

type User = Awaited<ReturnType<typeof renderApp>>['user'];

function serve(detail: LoanDetail = loanDetail(), answer: () => Response = () => HttpResponse.json(simulation())) {
  let current = detail;
  const asked: components['schemas']['LoanSimulationBody'][] = [];
  const recorded: components['schemas']['LoanRepaymentBody'][] = [];
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
    http.post('*/api/v1/realestate/loans/:id/simulations', async ({ request }) => {
      asked.push((await request.json()) as components['schemas']['LoanSimulationBody']);
      return answer();
    }),
    http.post('*/api/v1/realestate/loans/:id/repayments', async ({ request }) => {
      const body = (await request.json()) as components['schemas']['LoanRepaymentBody'];
      recorded.push(body);
      current = { ...current, repayments: [{ id: 'r9', onDate: body.onDate!, amount: '10000.00', effect: body.effect!, penalty: '300.00', note: null, version: 1 }] };
      return HttpResponse.json({ id: 'r9' }, { status: 201 });
    }),
  );
  return { asked, recorded };
}

const PAGE = `/loans/${LOAN}?tab=operations`;
const region = () => screen.findByRole('region', { name: 'Simuler un remboursement anticipé' });
const result = () => screen.findByRole('table', { name: 'Résultat de la simulation' });
const line = async (name: string) => within(await result()).getByRole('rowheader', { name }).closest('tr')!;

async function simulate(user: User, amount = '10000', penalty = '300') {
  const section = await region();
  await user.type(within(section).getByLabelText('Montant à rembourser'), amount);
  if (penalty) await user.type(within(section).getByLabelText('Pénalité'), penalty);
  await user.click(within(section).getByRole('button', { name: 'Simuler' }));
}

describe('the simulation', () => {
  beforeAll(async () => { await import('../LoanDetailPage'); }, 30_000);
  // Its date defaults to the browser's today: pinned to 2026-10-12.
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 9, 12, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('compares both effects side by side, every figure the server’s, and announces it', async () => {
    const { asked } = serve();
    const { user, container } = await renderApp(PAGE);

    expect(within(await region()).getByLabelText('Date du remboursement')).toHaveValue('12/10/2026');
    expect(within(await region()).getByText('Facultatif. Saisie par vous, jamais calculée.')).toBeInTheDocument();
    await simulate(user);

    const headers = within(await result()).getAllByRole('columnheader').map((h) => h.textContent);
    expect(headers).toEqual(['Indicateur', 'Réduire la durée', 'Réduire la mensualité']);
    expect(await line('Première mensualité après la date')).toHaveTextContent('de 1 159,92 € à 1 088,43 €');
    expect(await line('Dernière échéance')).toHaveTextContent('de 15 octobre 2045 à 15 juillet 2044');
    expect(await line('Nombre d’échéances')).toHaveTextContent('de 240 à 226');
    expect(await line('Intérêts sur toute la durée')).toHaveTextContent('de 78 380,66 € à 71 814,08 €');
    expect(await line('Intérêts économisés')).toHaveTextContent('6 566,58 €');
    expect(await line('Économie nette (pénalité déduite)')).toHaveTextContent('+6 266,58 €');
    expect(toasts()).toHaveTextContent('Simulation calculée : comparez les deux effets ci-dessous.');
    // No effect sent: the server answers both.
    expect(asked).toEqual([{ on: '2026-10-12', amount: '10000', penalty: '300' }]);
    await expectNoAxeViolations(container);
  });

  it('shows a negative net saving with its sign', async () => {
    const outcomes = simulation().outcomes.map((o) => ({ ...o, netSaving: o.effect === 'reduce_term' ? '-1566.58' : '-1669.34' }));
    serve(loanDetail(), () => HttpResponse.json(simulation({ penalty: '8000.00', outcomes } satisfies Partial<LoanSimulation>)));
    const { user } = await renderApp(PAGE);

    await simulate(user, '10000', '8000');

    const net = await line('Économie nette (pénalité déduite)');
    expect(net).toHaveTextContent('-1 566,58 €');
    expect(net).toHaveTextContent('-1 669,34 €');
  });

  it('shows a refusal under the field it names', async () => {
    serve(loanDetail(), () => HttpResponse.json(
      { status: 422, code: 'realestate.event_above_balance', title: 'x', errors: [{ path: 'amount', code: 'realestate.event_above_balance', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp(PAGE);

    await simulate(user, '500000', '');

    const section = await region();
    expect(await within(section).findByText('Ce remboursement dépasse le capital restant dû.')).toBeInTheDocument();
    expect(within(section).getByLabelText('Montant à rembourser')).toHaveAttribute('aria-invalid', 'true');
  });

  it('clears the result when an input changes', async () => {
    serve();
    const { user } = await renderApp(PAGE);

    await simulate(user);
    await result();
    await user.type(within(await region()).getByLabelText('Montant à rembourser'), '0');

    expect(screen.queryByRole('table', { name: 'Résultat de la simulation' })).not.toBeInTheDocument();
  });

  it('opens the repayment dialog prefilled from a column, each press afresh; nothing is sent until submitted, then the result goes', async () => {
    const { recorded } = serve();
    const { user, container } = await renderApp(PAGE);

    await simulate(user);
    await user.click(within(await result()).getByRole('button', { name: 'Enregistrer ce remboursement en réduisant la mensualité' }));
    let form = within(await screen.findByRole('dialog', { name: 'Nouveau remboursement anticipé' }));
    await waitFor(() => expect(form.getByLabelText('Date')).toHaveFocus());
    expect(form.getByLabelText('Date')).toHaveValue('12/10/2026');
    expect(form.getByLabelText('Montant')).toHaveValue('10000');
    expect(form.getByLabelText('Pénalité')).toHaveValue('300');
    expect(form.getByRole('radio', { name: 'Réduire la mensualité' })).toBeChecked();
    expect(recorded).toEqual([]);

    // The dialog is modal: cancelled, the other column opens it afresh with its own effect.
    await user.click(form.getByRole('button', { name: 'Annuler' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await user.click(within(await result()).getByRole('button', { name: 'Enregistrer ce remboursement en réduisant la durée' }));
    form = within(await screen.findByRole('dialog', { name: 'Nouveau remboursement anticipé' }));
    expect(form.getByRole('radio', { name: 'Réduire la durée' })).toBeChecked();

    // Cancelled, then opened plainly: no stale simulation in it.
    await user.click(form.getByRole('button', { name: 'Annuler' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: 'Ajouter un remboursement' }));
    form = within(await screen.findByRole('dialog', { name: 'Nouveau remboursement anticipé' }));
    expect(form.getByLabelText('Montant')).toHaveValue('');
    expect(form.getByRole('radio', { name: 'Réduire la durée' })).not.toBeChecked();
    await expectNoAxeViolations(container);
    await user.click(form.getByRole('button', { name: 'Annuler' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await user.click(within(await result()).getByRole('button', { name: 'Enregistrer ce remboursement en réduisant la durée' }));
    form = within(await screen.findByRole('dialog', { name: 'Nouveau remboursement anticipé' }));
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(recorded).toEqual([{ onDate: '2026-10-12', amount: '10000', effect: 'reduce_term', penalty: '300' }]));
    // The loan changed: a comparison of the loan before it no longer holds.
    await waitFor(() => expect(screen.queryByRole('table', { name: 'Résultat de la simulation' })).not.toBeInTheDocument());
  });

  it('does not reopen the repayment dialog with an old prefill when the tab is left and reached again', async () => {
    serve();
    const { user } = await renderApp(PAGE);

    await simulate(user);
    await user.click(within(await result()).getByRole('button', { name: 'Enregistrer ce remboursement en réduisant la durée' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Nouveau remboursement anticipé' }));
    await user.click(dialog.getByRole('button', { name: 'Annuler' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await user.click(screen.getByRole('tab', { name: 'Vue d’ensemble' }));
    await user.click(await screen.findByRole('tab', { name: 'Opérations' }));

    await region();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('is shown to a reader without write access, without « Enregistrer ce remboursement »', async () => {
    serve(loanDetail({ canWrite: false }));
    const { user, container } = await renderApp(PAGE);

    await simulate(user);

    await result();
    expect(screen.queryByRole('button', { name: /Enregistrer ce remboursement/ })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('is absent from an ended loan', async () => {
    serve(loanDetail({ ended: true, figures: { ...loanDetail().figures, nextPaymentOn: null, nextPaymentTotal: null } }));
    await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    expect(screen.queryByRole('region', { name: 'Simuler un remboursement anticipé' })).not.toBeInTheDocument();
  });
});
