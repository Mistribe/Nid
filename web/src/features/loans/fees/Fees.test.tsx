import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { LoanDetail, LoanFee } from '../api';
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

const region = () => screen.findByRole('region', { name: 'Frais' });
const rows = async () => within(await screen.findByRole('table', { name: 'Frais du crédit' })).getAllByRole('row').slice(1);

describe('fees', () => {
  beforeAll(async () => { await import('../LoanDetailPage'); }, 30_000);

  it('lists the fees by date; a linked penalty has no action and says where it is changed', async () => {
    serve(loanDetail({ fees: [FILE, PENALTY], repayments: [REPAYMENT] }));
    const { container } = await renderApp(`/loans/${LOAN}`);

    const [file, penalty] = await rows();
    expect(file).toHaveTextContent('15 octobre 2025');
    expect(file).toHaveTextContent('Frais de dossier');
    expect(file).toHaveTextContent('1 000,00 €');
    expect(file).toHaveTextContent('dossier');
    expect(within(file!).getByRole('button', { name: 'Corriger' })).toBeInTheDocument();
    expect(penalty).toHaveTextContent('Pénalité de remboursement anticipé');
    expect(penalty).toHaveTextContent('Se modifie avec son remboursement');
    expect(within(penalty!).queryByRole('button')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says when there is none', async () => {
    serve(loanDetail());
    await renderApp(`/loans/${LOAN}`);

    expect(await within(await region()).findByText('Aucun frais')).toBeInTheDocument();
  });

  it('adds fees without offering the penalty kind, announces them and focuses the section', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(`/loans/${LOAN}`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter des frais' }));
    const form = screen.getByRole('form', { name: 'Nouveaux frais' });
    expect(within(form).getByLabelText('Nature')).toHaveFocus();
    expect(within(form).getAllByRole('option').map((o) => o.textContent)).toEqual(['Frais de dossier', 'Garantie', 'Renégociation', 'Autre']);
    expect(within(form).getByLabelText('Date')).toHaveValue(todayIso());
    await user.selectOptions(within(form).getByLabelText('Nature'), 'guarantee');
    await user.type(within(form).getByLabelText('Montant'), '1500,50');
    await user.clear(within(form).getByLabelText('Date'));
    await user.type(within(form).getByLabelText('Date'), '2025-10-20');
    await user.type(within(form).getByLabelText('Note'), 'caution');
    await expectNoAxeViolations(container);
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Frais ajoutés.'));
    expect(sent).toEqual([expect.objectContaining({ method: 'POST', body: { kind: 'guarantee', amount: '1500.50', onDate: '2025-10-20', note: 'caution' } })]);
    expect(screen.getByRole('heading', { name: 'Frais' })).toHaveFocus();
    await waitFor(async () => expect((await rows())[1]).toHaveTextContent('1 500,50 €'));
  });

  it('corrects a fee inline with the version it read; an emptied note is left out', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/loans/${LOAN}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Corriger' }));
    const form = screen.getByRole('form', { name: 'Corriger les frais' });
    expect(within(form).getByLabelText('Montant')).toHaveValue('1000.00');
    await user.clear(within(form).getByLabelText('Montant'));
    await user.type(within(form).getByLabelText('Montant'), '1200');
    await user.clear(within(form).getByLabelText('Note'));
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Frais corrigés.'));
    expect(sent[0]).toMatchObject({ method: 'PUT', body: { kind: 'file', amount: '1200', onDate: '2025-10-15', version: 1 } });
    expect(sent[0]!.body).not.toHaveProperty('note');
    expect(sent[0]!.url).toContain('/fees/f1');
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('1 200,00 €'));
  });

  it('keeps the kind of an unlinked penalty when it is corrected', async () => {
    serve(loanDetail({ fees: [{ ...PENALTY, repaymentId: null }] }));
    const { user } = await renderApp(`/loans/${LOAN}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Corriger' }));

    expect(within(screen.getByRole('form', { name: 'Corriger les frais' })).getByLabelText('Nature')).toHaveValue('early_repayment_penalty');
  });

  it('deletes a fee after a confirmation, with the version it read', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/loans/${LOAN}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Supprimer' }));
    expect(screen.getByText('Ces frais seront supprimés.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Frais supprimés.'));
    expect(await within(await region()).findByText('Aucun frais')).toBeInTheDocument();
    expect(sent[0]!.url).toContain('version=1');
  });

  it('says a fee changed meanwhile, and « Recharger » closes the form onto the refreshed row', async () => {
    const { change } = serve();
    const { user } = await renderApp(`/loans/${LOAN}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Corriger' }));
    change('f1', { amount: '1100.00', version: 2 });
    const form = screen.getByRole('form', { name: 'Corriger les frais' });
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    expect(await within(form).findByText('Ces frais ont changé entre-temps : rechargez la page.')).toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(screen.queryByRole('form', { name: 'Corriger les frais' })).not.toBeInTheDocument());
    expect((await rows())[0]).toHaveTextContent('1 100,00 €');
    await waitFor(async () => expect(within((await rows())[0]!).getByRole('button', { name: 'Corriger' })).toHaveFocus());
  });

  it('gives a reader the fees without any action', async () => {
    serve(loanDetail({ canWrite: false, fees: [FILE] }));
    await renderApp(`/loans/${LOAN}`);

    const section = await region();
    await rows();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });
});
