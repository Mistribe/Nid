import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { LoanDetail } from '../api';
import { agreeing, bankRows, bankTable, LOAN, loanDetail, schedule, withTable } from '../testData';

/** A spreadsheet's copy: a header, then the golden case's first payment. */
const TEXT = 'Date;Échéance;Intérêts;Capital;CRD\n15/11/2025;1159,92;583,33;576,59;199423,41';

type Sent = { method: string; url: string; body?: unknown };
type Refusals = Partial<Record<'preview' | 'put' | 'delete', () => Response>>;

/**
 * The loan's page against a server that answers the table's routes and records what it was sent. A refusal answers once; then
 * the route answers as the server would: the table stored (the version moved), deleted, or the reference changed.
 */
function serve(initial: LoanDetail = loanDetail(), refusals: Refusals = {}) {
  let current = initial;
  const sent: Sent[] = [];
  const refuse = (route: keyof Refusals) => { const answer = refusals[route]; delete refusals[route]; return answer?.(); };
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
    http.post('*/api/v1/realestate/loans/:id/bank-schedule/preview', async ({ request }) => {
      sent.push({ method: 'POST', url: request.url, body: await request.json() });
      return refuse('preview') ?? HttpResponse.json({ rows: bankRows().slice(0, 1), comparison: agreeing() });
    }),
    http.put('*/api/v1/realestate/loans/:id/bank-schedule', async ({ request }) => {
      sent.push({ method: 'PUT', url: request.url, body: await request.json() });
      const refused = refuse('put');
      if (refused) return refused;
      current = { ...current, bankSchedule: bankTable(), comparison: agreeing(), contract: { ...current.contract, version: current.contract.version + 1 } };
      return HttpResponse.json({ id: LOAN });
    }),
    http.delete('*/api/v1/realestate/loans/:id/bank-schedule', ({ request }) => {
      sent.push({ method: 'DELETE', url: request.url });
      const refused = refuse('delete');
      if (refused) return refused;
      current = { ...current, scheduleSource: 'computed', bankSchedule: null, comparison: null };
      return new HttpResponse(null, { status: 204 });
    }),
    http.patch('*/api/v1/realestate/loans/:id', async ({ request }) => {
      const body = (await request.json()) as { scheduleSource?: string };
      sent.push({ method: 'PATCH', url: request.url, body });
      current = { ...current, scheduleSource: body.scheduleSource ?? current.scheduleSource };
      return HttpResponse.json({ id: LOAN });
    }),
  );
  return { sent };
}

// The section is lazy: under the full suite's load its first render can outlast findBy's default second.
const PAGE = `/loans/${LOAN}?tab=schedule`;
const region = () => screen.findByRole('region', { name: 'Tableau de la banque' });
const pasteForm = () => screen.getByRole('form', { name: 'Tableau de la banque à comparer' });

describe('the bank’s table', () => {
  // The route's chunk and the section's own, loaded cold inside a first test, race its timeouts under load.
  beforeAll(async () => { await import('../LoanDetailPage'); await import('./BankScheduleSection'); }, 30_000);

  it('pastes a table, compares it, stores it, then reads its period and comparison', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(PAGE);

    const section = await region();
    expect(within(section).getByText('Aucun tableau de la banque enregistré.')).toBeInTheDocument();
    await user.click(within(section).getByRole('button', { name: 'Importer le tableau de la banque' }));
    await user.click(within(pasteForm()).getByLabelText('Tableau copié'));
    await user.paste(TEXT);
    await user.click(within(pasteForm()).getByRole('button', { name: 'Comparer' }));

    const read = await within(pasteForm()).findByRole('table', { name: 'Lignes lues' });
    expect(within(read).getAllByRole('row')[1]).toHaveTextContent('15 novembre 2025');
    expect(within(read).getAllByRole('row')[1]).toHaveTextContent('199 423,41 €');
    expect(within(pasteForm()).getByText('Les deux concordent (écarts d’arrondi seulement).')).toBeInTheDocument();
    expect(sent[0]).toMatchObject({ method: 'POST', body: { text: TEXT } });
    await expectNoAxeViolations(container);
    await user.click(within(pasteForm()).getByRole('button', { name: 'Enregistrer le tableau' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Tableau de la banque enregistré.'));
    expect(sent[1]).toMatchObject({ method: 'PUT', body: { text: TEXT, version: 3 } });
    expect(await within(await region()).findByText('Du 15 novembre 2025 au 15 janvier 2026 (3 échéances)')).toBeInTheDocument();
    expect(screen.queryByRole('form', { name: 'Tableau de la banque à comparer' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Tableau de la banque' })).toHaveFocus();
    await expectNoAxeViolations(container);
  });

  /** Review Focus 5: the payment holds the insurance and the table has no insurance column. */
  it('shows each refused line with its reasons in French, under the line it quotes', async () => {
    serve(loanDetail(), {
      preview: () => HttpResponse.json({
        status: 422, code: 'realestate.bank_schedule_invalid', title: 'Some lines of the table cannot be read.',
        errors: [
          { path: 'rows[1].payment', code: 'realestate.bank_schedule_invalid', message: 'The payment is the interest plus the principal, without insurance.' },
          { path: 'rows[2]', code: 'realestate.bank_schedule_invalid', message: 'A line has 5 or 6 columns.' },
        ],
      }, { status: 422 }),
    });
    const { user, container } = await renderApp(PAGE);

    await user.click(within(await region()).getByRole('button', { name: 'Importer le tableau de la banque' }));
    await user.click(within(pasteForm()).getByLabelText('Tableau copié'));
    await user.paste('Date;Échéance;Intérêts;Capital;CRD\n15/11/2025;1189,92;583,33;576,59;199423,41\n15/12/2025;1159,92');
    await user.click(within(pasteForm()).getByRole('button', { name: 'Comparer' }));

    const lines = within(await within(pasteForm()).findByRole('list', { name: 'Lignes refusées' })).getAllByRole('listitem');
    expect(lines).toHaveLength(2);
    expect(lines[0]).toHaveTextContent('Ligne 2 : 15/11/2025;1189,92;583,33;576,59;199423,41');
    expect(lines[0]).toHaveTextContent('si elle comprend l’assurance, ajoutez la colonne de l’assurance');
    expect(lines[1]).toHaveTextContent('Ligne 3 : 15/12/2025;1159,92');
    expect(lines[1]).toHaveTextContent('Une ligne compte 5 ou 6 colonnes');
    expect(within(pasteForm()).getByLabelText('Tableau copié')).toHaveAttribute('aria-invalid', 'true');
    expect(within(pasteForm()).queryByRole('button', { name: 'Enregistrer le tableau' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('warns when the stored table no longer fits the loan', async () => {
    serve(withTable({ scheduleSource: 'bank', warnings: [{ code: 'bank_schedule_unfit', row: 0 }] }));
    const { container } = await renderApp(PAGE);

    const warnings = within(await screen.findByRole('list', { name: 'Avertissements' })).getAllByRole('listitem');
    expect(warnings[0]).toHaveTextContent('Le tableau de la banque ne correspond plus à ce crédit : le calcul sert de référence. Remplacez ou supprimez le tableau.');
    await region();
    await expectNoAxeViolations(container);
  });

  /** Review Focus 4: the calculation as the reference, drifting from the stored table. */
  it('reads the comparison, warns while the calculation is the reference, and chooses the bank', async () => {
    const diverging = agreeing({ maxGap: '1000.00', maxGapOn: '2026-01-15', divergesOn: '2026-01-15', interestGap: '12.50', rowsBeyondRounding: 1 });
    const { sent } = serve(withTable({ comparison: diverging, warnings: [{ code: 'schedule_mismatch', row: 3 }] }));
    const { user, container } = await renderApp(PAGE);

    const warnings = within(await screen.findByRole('list', { name: 'Avertissements' })).getAllByRole('listitem');
    expect(warnings[0]).toHaveTextContent('Le calcul s’écarte du tableau de la banque à partir du 15 janvier 2026');
    const section = await region();
    const summary = within(section).getByRole('list', { name: 'Comparaison avec le calcul' });
    expect(summary).toHaveTextContent('Écart maximal : 1 000,00 €, le 15 janvier 2026');
    expect(summary).toHaveTextContent('Le capital restant dû diverge de plus de 1,00 € à partir du 15 janvier 2026.');
    expect(summary).toHaveTextContent('Écart cumulé d’intérêts : +12,50 €');
    const reference = within(section).getByRole('form', { name: 'Référence' });
    expect(within(reference).getByRole('radio', { name: 'Calcul' })).toBeChecked();
    await user.click(within(reference).getByRole('radio', { name: 'Tableau de la banque' }));
    await user.click(within(reference).getByRole('button', { name: 'Choisir cette référence' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Le tableau de la banque est la référence.'));
    expect(sent).toEqual([expect.objectContaining({
      method: 'PATCH', body: { clearLender: false, clearNote: false, clearProperty: false, scheduleSource: 'bank' },
    })]);
    await expectNoAxeViolations(container);
  });

  it('marks the bank’s rows in the schedule, in words', async () => {
    serve(withTable({ scheduleSource: 'bank' }));
    const plan = schedule();
    server.use(http.get('*/api/v1/realestate/loans/:id/schedule',
      () => HttpResponse.json({ ...plan, rows: plan.rows.map((r) => ({ ...r, fromBank: r.number <= 3 })) })));
    const { container } = await renderApp(PAGE);

    const table = await screen.findByRole('table', { name: 'Échéancier par année' });
    const row = (date: string) => within(table).getAllByRole('row').find((r) => r.textContent?.includes(date));
    expect(row('15 janvier 2026')).toHaveTextContent('Banque');
    expect(row('15 février 2026')).not.toHaveTextContent('Banque');
    await expectNoAxeViolations(container);
  });

  it('deletes the table after a confirmation; a stale version offers « Recharger »', async () => {
    const { sent } = serve(withTable({ scheduleSource: 'bank' }), {
      delete: () => HttpResponse.json({ status: 409, code: 'realestate.loan_conflict', title: 'Changed.' }, { status: 409 }),
    });
    const { user, container } = await renderApp(PAGE);

    await user.click(within(await region()).getByRole('button', { name: 'Supprimer le tableau' }));
    expect(await screen.findByText('Le tableau de la banque sera supprimé et le calcul redeviendra la référence.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    expect(await within(await region()).findByText('Ce crédit a changé entre-temps : rechargez la page.')).toBeInTheDocument();
    await user.click(within(await region()).getByRole('button', { name: 'Recharger' }));
    await user.click(await within(await region()).findByRole('button', { name: 'Supprimer le tableau' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Tableau de la banque supprimé.'));
    expect(await within(await region()).findByText('Aucun tableau de la banque enregistré.')).toBeInTheDocument();
    expect(sent.filter((s) => s.method === 'DELETE').map((s) => new URL(s.url).searchParams.get('version'))).toEqual(['3', '3']);
    await expectNoAxeViolations(container);
  });

  it('gives a reader the period, the comparison and the reference as a sentence, without any action', async () => {
    serve(withTable({ canWrite: false, scheduleSource: 'bank' }));
    const { container } = await renderApp(PAGE);

    const section = await region();
    expect(within(section).getByText('Référence : Tableau de la banque')).toBeInTheDocument();
    expect(within(section).getByText('Les deux concordent (écarts d’arrondi seulement).')).toBeInTheDocument();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('radio')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('tells a reader no table is stored, without offering one', async () => {
    serve(loanDetail({ canWrite: false }));
    const { container } = await renderApp(PAGE);

    const section = await region();
    expect(within(section).getByText('Aucun tableau de la banque enregistré.')).toBeInTheDocument();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });
});
