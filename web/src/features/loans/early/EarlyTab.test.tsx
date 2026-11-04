import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { LoansSimulation } from '../api';
import { CAR, earlySimulation, household, LOAN, loanDetail, overview, schedule } from '../testData';

/** Each body the simulation was sent, and its answer (`answer` may depend on the body). */
function serve(answer: (body: Record<string, unknown>) => LoansSimulation | Response = () => earlySimulation()) {
  const sent: Record<string, unknown>[] = [];
  server.use(
    http.get('*/api/v1/realestate/loans/overview', () => HttpResponse.json(household())),
    http.post('*/api/v1/realestate/loans/overview/simulations', async ({ request }) => {
      const body = await request.json() as Record<string, unknown>;
      sent.push(body);
      const out = answer(body);
      return out instanceof Response ? out : HttpResponse.json(out);
    }),
  );
  return sent;
}

const region = (name: string | RegExp) => screen.findByRole('region', { name });
/** « Où vont les 5 000,00 € », its spaces the narrow ones `Intl` writes. */
const WHERE = /^Où vont les 5\s000,00\s€$/;

describe('Crédits › rembourser plus tôt', () => {
  beforeAll(async () => { await import('../LoansPage'); await import('./EarlyTab'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 9, 21, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('opens from ?tab=early and sends the split the person chose, without recording anything', async () => {
    const sent = serve();
    const { user, container } = await renderApp('/loans?tab=early');

    const form = await region('Simuler un remboursement anticipé');
    expect(screen.getByRole('tab', { name: 'Rembourser plus tôt' })).toHaveAttribute('aria-selected', 'true');
    expect(within(form).getByText('Réparti sur les crédits en cours. Rien n’est enregistré.')).toBeInTheDocument();
    await user.type(within(form).getByLabelText('Montant'), '5000');
    await user.click(within(form).getByRole('button', { name: 'Simuler' }));

    await waitFor(() => expect(sent).toEqual([{ scope: 'household', on: '2026-10-21', amount: '5000', split: 'highest_rate', effect: 'reduce_term' }]));
    const figures = (await screen.findByText('Économie nette')).closest('dl')!;
    expect(figures).toHaveTextContent('+1 855,50 €1 855,50 € d’intérêts, 0,00 € de pénalité');
    expect(figures).toHaveTextContent('Mensualité du foyer1 159,92 €au lieu de 2 159,92 € dès décembre 2026');
    expect(figures).toHaveTextContent('Sans crédit enjuillet 2045au lieu de octobre 2045');
    expect(within(figures).getByText('Économie nette').nextElementSibling!.className).not.toMatch(/text-status-|text-destructive/);
    const where = await region(WHERE);
    const [car, home] = within(within(where).getByRole('table')).getAllByRole('row').slice(1);
    expect(car).toHaveTextContent('Prêt auto5 %3 000,00 €soldé en novembre 2026');
    expect(home).toHaveTextContent('2 000,00 €15 juillet 2045 au lieu de 15 octobre 2045');
    await expectNoAxeViolations(container);
  });

  it('opens the choice of the loan for « Un crédit choisi » and sends it', async () => {
    const sent = serve();
    const { user } = await renderApp('/loans?tab=early');

    const form = await region('Simuler un remboursement anticipé');
    expect(within(form).queryByLabelText('Crédit')).not.toBeInTheDocument();
    await user.click(within(form).getByRole('radio', { name: /Un crédit choisi/ }));
    await user.type(within(form).getByLabelText('Montant'), '1000');
    await user.click(within(form).getByRole('button', { name: 'Simuler' }));
    expect(await within(form).findByText('Choisissez un crédit en cours.')).toBeInTheDocument();
    await pick(user, 'Crédit', 'Prêt auto');
    await user.click(within(form).getByRole('radio', { name: 'Réduire la mensualité' }));
    await user.click(within(form).getByRole('button', { name: 'Simuler' }));

    await waitFor(() => expect(sent).toEqual([expect.objectContaining({ split: 'chosen', loanId: CAR, effect: 'reduce_payment' })]));
  });

  it('a changed penalty offers « Relancer » and sends it with the same question', async () => {
    const sent = serve((body) => (body.penalties ? earlySimulation({ penalties: { amount: '120.00', currency: 'EUR' } }) : earlySimulation()));
    const { user } = await renderApp('/loans?tab=early');

    const form = await region('Simuler un remboursement anticipé');
    await user.type(within(form).getByLabelText('Montant'), '5000');
    await user.click(within(form).getByRole('button', { name: 'Simuler' }));
    const where = await region(WHERE);
    expect(within(where).queryByRole('button', { name: 'Relancer la simulation' })).not.toBeInTheDocument();
    await user.type(within(where).getByLabelText('Pénalité de « Prêt maison »'), '120');

    await user.click(within(where).getByRole('button', { name: 'Relancer la simulation' }));

    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]).toEqual(expect.objectContaining({ amount: '5000', split: 'highest_rate', penalties: [{ loanId: LOAN, amount: '120' }] }));
    expect(await screen.findByText(/120,00 € de pénalité/)).toBeInTheDocument();
  });

  it('shows a refusal under its own field', async () => {
    serve(() => HttpResponse.json(
      { status: 422, code: 'request.validation', errors: [{ path: 'amount', code: 'realestate.event_above_balance', message: 'above' }] },
      { status: 422 }));
    const { user } = await renderApp('/loans?tab=early');

    const form = await region('Simuler un remboursement anticipé');
    await user.type(within(form).getByLabelText('Montant'), '999999');
    await user.click(within(form).getByRole('button', { name: 'Simuler' }));

    expect(await within(form).findByText('Ce remboursement dépasse le capital restant dû.')).toBeInTheDocument();
  });

  it('records nothing itself: each loan opens with its repayment prefilled, once', async () => {
    serve();
    server.use(
      // The loan's own path: a ':id' pattern would also answer the overview.
      http.get(`*/api/v1/realestate/loans/${LOAN}`, () => HttpResponse.json(loanDetail())),
      http.get(`*/api/v1/realestate/loans/${LOAN}/schedule`, () => HttpResponse.json(schedule())),
    );
    const { user, router } = await renderApp('/loans?tab=early');

    const form = await region('Simuler un remboursement anticipé');
    await user.type(within(form).getByLabelText('Montant'), '5000');
    await user.click(within(form).getByRole('button', { name: 'Simuler' }));
    await user.click(await screen.findByRole('button', { name: 'Enregistrer ces remboursements' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Enregistrer ces remboursements' }));
    await user.click(dialog.getByRole('link', { name: /Prêt maison : 2\s000,00\s€/ }));

    const add = within(await screen.findByRole('dialog', { name: 'Nouveau remboursement anticipé' }));
    expect(add.getByLabelText('Montant')).toHaveValue('2000.00');
    expect(add.getByLabelText('Date')).toHaveValue('15/11/2026');
    expect(add.getByRole('radio', { name: 'Réduire la durée' })).toBeChecked();
    await waitFor(() => expect(router.state.location.state.loanRepayment).toBeUndefined());
    expect(router.state.location.search).toMatchObject({ tab: 'operations' });
  });

  it('is not offered without a running loan', async () => {
    server.use(http.get('*/api/v1/realestate/loans/overview', () => HttpResponse.json(overview({ endedCount: 1 }))));
    await renderApp('/loans');

    await screen.findByRole('heading', { name: 'Aucun crédit en cours' });
    expect(screen.queryByRole('tab', { name: 'Rembourser plus tôt' })).not.toBeInTheDocument();
  });
});
