import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { LoanDetail, LoanRepayment } from '../api';
import { LOAN, loanDetail, schedule } from '../testData';

type Body = components['schemas']['LoanRepaymentBody'];
const BONUS: LoanRepayment = { id: 'r1', onDate: '2026-03-20', amount: '10000.00', effect: 'reduce_payment', penalty: '300.00', note: 'prime', version: 3 };
/** An amount as the server writes it back: two decimals. */
const written = (typed: string) => (typed.includes('.') ? typed : `${typed}.00`);
const refusal = (path: string, code: string) =>
  HttpResponse.json({ status: 422, code, title: 'x', errors: [{ path, code, message: 'x' }] }, { status: 422 });

function serve(initial: LoanDetail = loanDetail({ repayments: [BONUS] }), refuse?: (method: string) => Response | undefined) {
  let current = initial;
  let next = 2;
  const sent: { method: string; url: string; body?: Body }[] = [];
  const view = (id: string, b: Body, version: number): LoanRepayment => ({
    id, onDate: b.onDate!, amount: written(b.amount!), effect: b.effect!, penalty: b.penalty ? written(b.penalty) : null, note: b.note ?? null, version,
  });
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
    http.post('*/api/v1/realestate/loans/:id/repayments', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'POST', url: request.url, body });
      const refused = refuse?.('POST');
      if (refused) return refused;
      const id = `r${next++}`;
      current = { ...current, repayments: [...current.repayments, view(id, body, 1)].sort((a, b) => a.onDate.localeCompare(b.onDate)) };
      return HttpResponse.json({ id }, { status: 201 });
    }),
    http.put('*/api/v1/realestate/loans/:id/repayments/:eventId', async ({ request, params }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'PUT', url: request.url, body });
      const old = current.repayments.find((r) => r.id === params.eventId);
      if (!old) return HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 });
      if (old.version !== body.version) return HttpResponse.json({ status: 409, code: 'realestate.repayment_conflict', title: 'Changed.' }, { status: 409 });
      current = { ...current, repayments: current.repayments.map((r) => (r.id === old.id ? view(old.id, body, old.version + 1) : r)) };
      return HttpResponse.json({ id: old.id });
    }),
    http.delete('*/api/v1/realestate/loans/:id/repayments/:eventId', ({ request, params }) => {
      sent.push({ method: 'DELETE', url: request.url });
      const refused = refuse?.('DELETE');
      if (refused) return refused;
      current = { ...current, repayments: current.repayments.filter((r) => r.id !== params.eventId) };
      return new HttpResponse(null, { status: 204 });
    }),
  );
  /** Someone else saves the first repayment: its stored version moves on. */
  const bump = () => { current = { ...current, repayments: current.repayments.map((r, i) => (i === 0 ? { ...r, version: r.version + 1 } : r)) }; };
  return { sent, bump };
}

const PAGE = `/loans/${LOAN}?tab=operations`;
const ROW = '20 mars 2026';
const region = () => screen.findByRole('region', { name: 'Remboursements anticipés' });
type User = Awaited<ReturnType<typeof renderApp>>['user'];
/** The row's ⋯ menu, opened, then the item picked. */
const rowAction = async (user: User, row: HTMLElement, item: string) => {
  await user.click(within(row).getByRole('button', { name: new RegExp(`^Actions pour le ${ROW}`) }));
  await user.click(await screen.findByRole('menuitem', { name: item }));
};
const dialogOf = async (name: string) => within(await screen.findByRole('dialog', { name }));
const rows = async () => within(await screen.findByRole('table', { name: 'Remboursements anticipés du crédit' })).getAllByRole('row').slice(1);

describe('early repayments', () => {
  beforeAll(async () => { await import('../LoanDetailPage'); }, 30_000);

  it('lists each repayment with its amount, effect, penalty and note', async () => {
    serve();
    const { container } = await renderApp(PAGE);

    const [bonus] = await rows();
    expect(bonus).toHaveTextContent('20 mars 2026');
    expect(bonus).toHaveTextContent('10 000,00 €');
    expect(bonus).toHaveTextContent('Mensualité réduite');
    expect(bonus).toHaveTextContent('300,00 €');
    expect(bonus).toHaveTextContent('prime');
    await expectNoAxeViolations(container);
  });

  it('says when there is none', async () => {
    serve(loanDetail());
    await renderApp(PAGE);

    expect(await within(await region()).findByText('Aucun remboursement anticipé')).toBeInTheDocument();
  });

  it('adds a repayment with its penalty once its effect is picked', async () => {
    const { sent } = serve(loanDetail());
    const { user, container } = await renderApp(PAGE);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter un remboursement' }));
    const form = await dialogOf('Nouveau remboursement anticipé');
    await waitFor(() => expect(form.getByLabelText('Date')).toHaveFocus());
    expect(form.getByLabelText('Date')).toHaveValue(todayIso().split('-').reverse().join('/'));
    expect(form.getByRole('radio', { name: 'Réduire la durée' })).not.toBeChecked();
    expect(form.getByRole('radio', { name: 'Réduire la mensualité' })).not.toBeChecked();
    expect(form.getByText('Facultatif. Saisie par vous, jamais calculée.')).toBeInTheDocument();
    await user.type(form.getByLabelText('Montant'), '5000');
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    expect(await form.findByText('Choisissez l’effet du remboursement.')).toBeInTheDocument();
    expect(sent).toEqual([]);
    await user.click(form.getByRole('radio', { name: 'Réduire la durée' }));
    await user.type(form.getByLabelText('Pénalité'), '150');
    await expectNoAxeViolations(container);
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Remboursement anticipé ajouté.'));
    expect(sent).toEqual([expect.objectContaining({ method: 'POST', body: { onDate: todayIso(), amount: '5000', effect: 'reduce_term', penalty: '150' } })]);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Remboursements anticipés' })).toHaveFocus();
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('5 000,00 €'));
  });

  it('corrects a repayment with its penalty prefilled, and leaves an emptied penalty out so the server deletes it', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, 'Corriger');
    const form = await dialogOf('Corriger le remboursement anticipé');
    expect(form.getByLabelText('Pénalité')).toHaveValue('300.00');
    expect(form.getByRole('radio', { name: 'Réduire la mensualité' })).toBeChecked();
    await user.clear(form.getByLabelText('Pénalité'));
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Remboursement anticipé corrigé.'));
    expect(sent[0]).toMatchObject({ method: 'PUT', body: { onDate: '2026-03-20', amount: '10000.00', effect: 'reduce_payment', note: 'prime', version: 3 } });
    expect(sent[0]!.body).not.toHaveProperty('penalty');
    expect(sent[0]!.url).toContain('/repayments/r1');
  });

  it('sends the version the form opened on, so a change made meanwhile is a conflict, not an overwrite', async () => {
    const { sent, bump } = serve();
    const { user, queryClient } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, 'Corriger');
    const form = await dialogOf('Corriger le remboursement anticipé');
    bump();
    await queryClient.invalidateQueries();
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    expect(await form.findByText('Ce remboursement a changé entre-temps : rechargez la page.')).toBeInTheDocument();
    expect(form.getByRole('button', { name: 'Recharger' })).toBeInTheDocument();
    expect(sent[0]!.body).toMatchObject({ version: 3 });
  });

  it('deletes a repayment after a confirmation that says its penalty goes with it', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, 'Supprimer');
    expect(await screen.findByText('Ce remboursement anticipé et sa pénalité de 300,00 € seront supprimés.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Remboursement anticipé supprimé.'));
    expect(await within(await region()).findByText('Aucun remboursement anticipé')).toBeInTheDocument();
    expect(sent[0]!.url).toContain('version=3');
  });

  it('asks a plain confirmation for a repayment without penalty', async () => {
    serve(loanDetail({ repayments: [{ ...BONUS, penalty: null }] }));
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, 'Supprimer');

    expect(await screen.findByText('Ce remboursement anticipé sera supprimé.')).toBeInTheDocument();
  });

  it('shows a refusal under the field it names', async () => {
    serve(loanDetail(), (method) => (method === 'POST' ? refusal('amount', 'realestate.event_above_balance') : undefined));
    const { user } = await renderApp(PAGE);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter un remboursement' }));
    const form = await dialogOf('Nouveau remboursement anticipé');
    await user.type(form.getByLabelText('Montant'), '500000');
    await user.click(form.getByRole('radio', { name: 'Réduire la durée' }));
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    expect(await form.findByText('Ce remboursement dépasse le capital restant dû.')).toBeInTheDocument();
    expect(form.getByLabelText('Montant')).toHaveAttribute('aria-invalid', 'true');
  });

  it('shows in the section an engine refusal of a deletion', async () => {
    serve(undefined, (method) => (method === 'DELETE' ? refusal('phases[0].paymentOverride', 'realestate.payment_below_interest') : undefined));
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, 'Supprimer');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    expect(await within(await region()).findByText('Cette mensualité ne couvre pas les intérêts : le prêt ne finirait jamais.')).toBeInTheDocument();
    expect((await rows())[0]).toHaveTextContent('10 000,00 €');
  });

  it('gives a reader the repayments without any action', async () => {
    serve(loanDetail({ canWrite: false, repayments: [BONUS] }));
    await renderApp(PAGE);

    const section = await region();
    await rows();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });
});
