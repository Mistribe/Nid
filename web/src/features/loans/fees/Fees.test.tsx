import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { todayIso } from '@/lib/today';
import type { LoanDetail, LoanFee } from '@/api/loans';
import { LOAN, loanDetail, schedule } from '../testData';

type Body = components['schemas']['LoanFeeBody'];
const FILE: LoanFee = { id: 'f1', kind: 'file', amount: '1000.00', onDate: '2025-10-15', note: 'dossier', repaymentId: null, version: 1 };
const PENALTY: LoanFee = { id: 'f2', kind: 'early_repayment_penalty', amount: '300.00', onDate: '2026-03-20', note: null, repaymentId: 'r1', version: 2 };
const REPAYMENT = { id: 'r1', onDate: '2026-03-20', amount: '10000.00', effect: 'reduce_payment', penalty: '300.00', note: null, version: 3 };
/** An amount as the server writes it back: two decimals. */
const written = (typed: string) => (typed.includes('.') ? typed : `${typed}.00`);

function serve(initial: LoanDetail = loanDetail({ fees: [FILE] })) {
  let current = initial;
  let next = 3;
  const sent: { method: string; url: string; body?: Body }[] = [];
  const view = (id: string, b: Body, version: number): LoanFee =>
    ({ id, kind: b.kind!, amount: written(b.amount!), onDate: b.onDate!, note: b.note ?? null, repaymentId: null, version });
  const byDate = (fees: LoanFee[]) => [...fees].sort((a, b) => a.onDate.localeCompare(b.onDate));
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
    http.post('*/api/v1/realestate/loans/:id/fees', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'POST', url: request.url, body });
      const id = `f${next++}`;
      current = { ...current, fees: byDate([...current.fees, view(id, body, 1)]) };
      return HttpResponse.json({ id }, { status: 201 });
    }),
    http.put('*/api/v1/realestate/loans/:id/fees/:feeId', async ({ request, params }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'PUT', url: request.url, body });
      const old = current.fees.find((f) => f.id === params.feeId);
      if (!old) return HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 });
      if (old.version !== body.version) return HttpResponse.json({ status: 409, code: 'realestate.fee_conflict', title: 'Changed.' }, { status: 409 });
      current = { ...current, fees: byDate(current.fees.map((f) => (f.id === old.id ? view(old.id, body, old.version + 1) : f))) };
      return HttpResponse.json({ id: old.id });
    }),
    http.delete('*/api/v1/realestate/loans/:id/fees/:feeId', ({ request, params }) => {
      sent.push({ method: 'DELETE', url: request.url });
      if (!current.fees.some((f) => f.id === params.feeId)) return HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 });
      current = { ...current, fees: current.fees.filter((f) => f.id !== params.feeId) };
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return { sent, change: (id: string, over: Partial<LoanFee>) => { current = { ...current, fees: current.fees.map((f) => (f.id === id ? { ...f, ...over } : f)) }; } };
}

const PAGE = `/loans/${LOAN}?tab=operations`;
const region = () => screen.findByRole('region', { name: 'Frais' });
type User = Awaited<ReturnType<typeof renderApp>>['user'];
/** The row's ⋯ menu, opened, then the item picked. */
const rowAction = async (user: User, row: HTMLElement, date: string, item: string) => {
  await user.click(within(row).getByRole('button', { name: new RegExp(`^Actions pour le ${date}`) }));
  await user.click(await screen.findByRole('menuitem', { name: item }));
};
const FILE_DATE = '15 octobre 2025';
const rows = async () => within(await screen.findByRole('table', { name: 'Frais du crédit' })).getAllByRole('row').slice(1);

describe('fees', () => {
  beforeAll(async () => { await import('../LoanDetailPage'); }, 30_000);

  it('lists the fees by date; a linked penalty has no action and says where it is changed', async () => {
    serve(loanDetail({ fees: [FILE, PENALTY], repayments: [REPAYMENT] }));
    const { container } = await renderApp(PAGE);

    const [file, penalty] = await rows();
    expect(file).toHaveTextContent('15 octobre 2025');
    expect(file).toHaveTextContent('Frais de dossier');
    expect(file).toHaveTextContent('1 000,00 €');
    expect(file).toHaveTextContent('dossier');
    expect(within(file!).getByRole('button', { name: new RegExp(`^Actions pour le ${FILE_DATE}`) })).toBeInTheDocument();
    expect(penalty).toHaveTextContent('Pénalité de remboursement anticipé');
    expect(penalty).toHaveTextContent('Se modifie avec son remboursement');
    expect(within(penalty!).queryByRole('button')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says when there is none', async () => {
    serve(loanDetail());
    await renderApp(PAGE);

    expect(await within(await region()).findByText('Aucun frais')).toBeInTheDocument();
  });

  it('adds fees without offering the penalty kind, announces them and focuses the section', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(PAGE);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter des frais' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Nouveaux frais' }));
    const kind = dialog.getByRole('combobox', { name: 'Nature' });
    await waitFor(() => expect(kind).toHaveFocus());
    await user.click(kind);
    expect(within(await screen.findByRole('listbox')).getAllByRole('option').map((o) => o.textContent)).toEqual(['Frais de dossier', 'Garantie', 'Renégociation', 'Autre']);
    await user.click(screen.getByRole('option', { name: 'Garantie' }));
    expect(dialog.getByLabelText('Date')).toHaveValue(todayIso().split('-').reverse().join('/'));
    await user.type(dialog.getByLabelText('Montant'), '1500,50');
    await user.clear(dialog.getByLabelText('Date'));
    await user.type(dialog.getByLabelText('Date'), '20/10/2025');
    await user.type(dialog.getByLabelText('Note'), 'caution');
    await expectNoAxeViolations(container);
    await user.click(dialog.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(toasts()).toHaveTextContent('Frais ajoutés.'));
    expect(sent).toEqual([expect.objectContaining({ method: 'POST', body: { kind: 'guarantee', amount: '1500.50', onDate: '2025-10-20', note: 'caution' } })]);
    expect(screen.getByRole('heading', { name: 'Frais' })).toHaveFocus();
    await waitFor(async () => expect((await rows())[1]).toHaveTextContent('1 500,50 €'));
  });

  it('corrects a fee in a dialog with the version it read; an emptied note is left out', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, FILE_DATE, 'Corriger');
    const dialog = within(await screen.findByRole('dialog', { name: 'Corriger les frais' }));
    expect(dialog.getByLabelText('Montant')).toHaveValue('1000,00');
    await user.clear(dialog.getByLabelText('Montant'));
    await user.type(dialog.getByLabelText('Montant'), '1200');
    await user.clear(dialog.getByLabelText('Note'));
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Frais corrigés.'));
    expect(sent[0]).toMatchObject({ method: 'PUT', body: { kind: 'file', amount: '1200', onDate: '2025-10-15', version: 1 } });
    expect(sent[0]!.body).not.toHaveProperty('note');
    expect(sent[0]!.url).toContain('/fees/f1');
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('1 200,00 €'));
  });

  it('keeps the kind of an unlinked penalty when it is corrected', async () => {
    serve(loanDetail({ fees: [{ ...PENALTY, repaymentId: null }] }));
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, '20 mars 2026', 'Corriger');

    expect(within(await screen.findByRole('dialog', { name: 'Corriger les frais' })).getByRole('combobox', { name: 'Nature' })).toHaveTextContent('Pénalité de remboursement anticipé');
  });

  it('deletes a fee after a confirmation, with the version it read', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, FILE_DATE, 'Supprimer');
    expect(await screen.findByText('Ces frais seront supprimés.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Frais supprimés.'));
    expect(await within(await region()).findByText('Aucun frais')).toBeInTheDocument();
    expect(sent[0]!.url).toContain('version=1');
  });

  it('says a fee changed meanwhile, and « Recharger » closes the dialog onto the refreshed row', async () => {
    const { change } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, FILE_DATE, 'Corriger');
    change('f1', { amount: '1100.00', version: 2 });
    const dialog = within(await screen.findByRole('dialog', { name: 'Corriger les frais' }));
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    expect(await dialog.findByText('Ces frais ont changé entre-temps : rechargez la page.')).toBeInTheDocument();
    await user.click(dialog.getByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect((await rows())[0]).toHaveTextContent('1 100,00 €');
    await waitFor(async () => expect(within((await rows())[0]!).getByRole('button', { name: new RegExp(`^Actions pour le ${FILE_DATE}`) })).toHaveFocus());
  });

  it('gives a reader the fees without any action', async () => {
    serve(loanDetail({ canWrite: false, fees: [FILE] }));
    await renderApp(PAGE);

    const section = await region();
    await rows();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });
});
