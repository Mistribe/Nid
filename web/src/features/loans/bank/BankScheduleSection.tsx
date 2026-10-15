import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Presentation } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Section } from '../../income/Section';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { BANK_PREVIEW, BANK_SCHEDULE, invalidateLoans, LOAN, type BankRow, type LoanComparison, type LoanDetail } from '../api';
import { money, signedMoney } from '../format';
import { presentRefusal } from '../refusal';
import { toWireBody } from '../settings/settingsBody';

const SOURCES = ['computed', 'bank'] as const;
const COLUMNS = ['dueDate', 'payment', 'interest', 'principal', 'insurance', 'crd'] as const;
type Column = (typeof COLUMNS)[number] | 'line';
const th = 'px-2 py-1 font-medium';
const num = 'px-2 py-1 text-right tabular-nums';

/** The pasted text's lines, cut as the server cuts them (`ReplaceLineEndings`): `rows[i]` is line i. */
const linesOf = (text: string) => text.split(/\r\n|[\n\r\f\u0085\u2028\u2029]/);

/** The refusals at `rows[i]` or `rows[i].column`, by line in line order; a refusal elsewhere (`text`) is no line's. */
function refusedLines(error: unknown): [number, Column[]][] {
  const byLine = new Map<number, Column[]>();
  for (const e of toProblem(error).errors ?? []) {
    const match = /^rows\[(\d+)\](?:\.(\w+))?$/.exec(e.path);
    if (!match) continue;
    const line = Number(match[1]);
    byLine.set(line, [...(byLine.get(line) ?? []), COLUMNS.find((c) => c === match[2]) ?? 'line']);
  }
  return [...byLine.entries()].sort(([a], [b]) => a - b);
}

/**
 * 3F2 design §4: the bank's table on the loan's page. Without one, « Importer le tableau de la banque »; with one, its period,
 * its comparison with the calculation (the server's figures, in words), the reference, « Remplacer le tableau » and
 * « Supprimer le tableau ». The section owns its live region and where the focus lands, as the fees'.
 */
export function BankScheduleSection({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const [pasting, setPasting] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const headingRef = useRef<HTMLHeadingElement>(null);
  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const table = loan.bankSchedule;

  return (
    <Section title={t('loan_bank.title')} headingRef={headingRef} announcement={announcement}>
      {table ? (
        <>
          <p>{t('loan_bank.period', { first: formatLocalDate(table.firstDueOn), last: formatLocalDate(table.lastDueOn), count: table.rows.length })}</p>
          {loan.comparison ? <Comparison currency={loan.currency} comparison={loan.comparison} /> : null}
          <Reference loan={loan} onStart={() => setAnnouncement('')} onDone={announce} />
        </>
      ) : <p>{t('loan_bank.none')}</p>}
      {loan.canWrite && !pasting ? (
        <div className="flex flex-wrap items-center gap-2">
          <Button type="button" variant="outline" onClick={() => { setAnnouncement(''); setPasting(true); }}>
            {t(table ? 'loan_bank.replace' : 'loan_bank.import')}
          </Button>
          {table ? <DeleteTable loan={loan} onStart={() => setAnnouncement('')} onDone={announce} /> : null}
        </div>
      ) : null}
      {loan.canWrite && pasting ? (
        <PasteForm loan={loan} onDone={(message) => { setPasting(false); if (message) announce(message); else headingRef.current?.focus(); }} />
      ) : null}
    </Section>
  );
}

/** The server's summary of the comparison (spec 3F2 §2) in words; the thresholds in the loan's currency. Nothing is computed here. */
function Comparison({ currency, comparison: c }: { currency: string; comparison: LoanComparison }) {
  const { t } = useTranslation();
  const threshold = money(currency, '1');
  const rounding = money(currency, '0.05');
  return (
    <ul aria-label={t('loan_bank.comparison')} className="space-y-1">
      <li>{c.maxGapOn
        ? t('loan_bank.max_gap_on', { amount: money(currency, c.maxGap), date: formatLocalDate(c.maxGapOn) })
        : t('loan_bank.max_gap', { amount: money(currency, c.maxGap) })}</li>
      <li>{c.divergesOn
        ? t('loan_bank.diverges', { threshold, date: formatLocalDate(c.divergesOn) })
        : c.rowsBeyondRounding > 0 ? t('loan_bank.close', { threshold, rounding, count: c.rowsBeyondRounding }) : t('loan_bank.agree')}</li>
      <li>{t('loan_bank.interest_gap', { amount: signedMoney(currency, c.interestGap) })}</li>
    </ul>
  );
}

/** « Référence » — two labelled radios and a button, never colour alone; a reader reads it as a sentence. */
function Reference({ loan, onStart, onDone }: { loan: LoanDetail; onStart: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [choice, setChoice] = useState<(typeof SOURCES)[number]>(loan.scheduleSource === 'bank' ? 'bank' : 'computed');
  const save = $api.useMutation('patch', LOAN, { onSuccess: () => invalidateLoans(queryClient) });
  const problem = save.error ? presentRefusal(t, save.error) : null;
  if (!loan.canWrite)
    return <p>{t('loan_bank.reference_is', { source: t(loan.scheduleSource === 'bank' ? 'loan_bank.source.bank' : 'loan_bank.source.computed') })}</p>;

  return (
    <form aria-label={t('loan_bank.reference')} noValidate className="space-y-2"
      onSubmit={(ev) => {
        ev.preventDefault();
        onStart();
        save.mutate({ params: { path: { id: loan.id } }, body: toWireBody({ scheduleSource: choice }) },
          { onSuccess: () => onDone(t(`loan_bank.reference_saved.${choice}`)) });
      }}>
      <fieldset className="space-y-1">
        <legend className="text-sm">{t('loan_bank.reference')}</legend>
        {SOURCES.map((s) => (
          <Field key={s} inline label={t(`loan_bank.source.${s}`)}>
            {(c) => <input {...c} type="radio" name="scheduleSource" value={s} checked={choice === s} onChange={() => setChoice(s)} />}
          </Field>
        ))}
      </fieldset>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => { save.reset(); return invalidateLoans(queryClient); }} /> : null}
        </>
      ) : null}
      <Button type="submit" disabled={save.isPending}>{t('loan_bank.reference_submit')}</Button>
    </form>
  );
}

/** « Supprimer le tableau », confirmed, with the loan's version as read; a stale version offers « Recharger ». */
function DeleteTable({ loan, onStart, onDone }: { loan: LoanDetail; onStart: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', BANK_SCHEDULE, {
    onSuccess: () => { void invalidateLoans(queryClient); onDone(t('loan_bank.deleted')); },
  });
  const problem = remove.error ? presentRefusal(t, remove.error) : null;
  return (
    <span className="flex flex-wrap items-center gap-2">
      <ConfirmAction label={t('loan_bank.delete')} confirmation={t('loan_bank.delete_confirm')} pending={remove.isPending}
        onConfirm={() => { onStart(); remove.mutate({ params: { path: { id: loan.id }, query: { version: loan.contract.version } } }); }} />
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => { remove.reset(); return invalidateLoans(queryClient); }} /> : null}
        </>
      ) : null}
    </span>
  );
}

/**
 * The pasted table: « Comparer » has the server read and compare it, nothing stored; its preview — the comparison and the rows
 * read — offers « Enregistrer le tableau » with the loan's version as read. A refused line is quoted with its reasons.
 */
function PasteForm({ loan, onDone }: { loan: LoanDetail; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [text, setText] = useState('');
  const compare = $api.useMutation('post', BANK_PREVIEW);
  const store = $api.useMutation('put', BANK_SCHEDULE, { onSuccess: () => invalidateLoans(queryClient) });
  const refused = compare.error ? refusedLines(compare.error) : [];
  const lines = linesOf(text);
  // A refusal no line owns: an empty or oversized text, a table a stored repayment cannot follow, or the store's own.
  const problem: Presentation | null = compare.error && refused.length === 0
    ? (toProblem(compare.error).code === 'realestate.bank_schedule_invalid'
      ? { kind: 'validation', detail: t('loan_bank.unreadable') }
      : presentRefusal(t, compare.error))
    : store.error ? presentRefusal(t, store.error) : null;
  const preview = compare.data;

  return (
    <form aria-label={t('loan_bank.form')} noValidate className="space-y-3"
      onSubmit={(ev) => { ev.preventDefault(); store.reset(); compare.mutate({ params: { path: { id: loan.id } }, body: { text } }); }}>
      <Field label={t('loan_bank.text')} hint={t('loan_bank.text_help')} error={refused.length > 0 ? t('loan_bank.has_refusals') : undefined}>
        {(c) => <textarea {...c} value={text} rows={8} className="w-full rounded-md border px-3 py-2 font-mono text-sm"
          onChange={(ev) => { setText(ev.target.value); compare.reset(); }} />}
      </Field>
      {refused.length > 0 ? (
        <ol aria-label={t('loan_bank.refused')} className="space-y-2">
          {refused.map(([line, columns]) => (
            <li key={line} className="space-y-1">
              <p className="font-mono text-sm">{t('loan_bank.line', { n: line + 1, text: lines[line] ?? '' })}</p>
              {columns.map((column) => <p key={column} className="text-sm text-destructive">{t(`loan_bank.refusal.${column}`)}</p>)}
            </li>
          ))}
        </ol>
      ) : null}
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => { store.reset(); return invalidateLoans(queryClient); }} /> : null}
        </>
      ) : null}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={compare.isPending || text.trim() === ''}>{t('loan_bank.compare')}</Button>
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('loan_bank.cancel')}</Button>
      </div>
      {preview ? (
        <div className="space-y-2">
          <Comparison currency={loan.currency} comparison={preview.comparison} />
          <PreviewRows rows={preview.rows} currency={loan.currency} />
          <Button type="button" disabled={store.isPending}
            onClick={() => store.mutate({ params: { path: { id: loan.id } }, body: { text, version: loan.contract.version } },
              { onSuccess: () => onDone(t('loan_bank.saved')) })}>
            {t('loan_bank.save')}
          </Button>
        </div>
      ) : null}
    </form>
  );
}

/** The rows as the server read them: dates and amounts formatted, nothing computed. */
function PreviewRows({ rows, currency }: { rows: BankRow[]; currency: string }) {
  const { t } = useTranslation();
  const m = (amount: string) => money(currency, amount);
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm" aria-label={t('loan_bank.preview')}>
        <thead>
          <tr>
            <th scope="col" className={`${th} text-left`}>{t('loan_detail.schedule.col_date')}</th>
            <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_payment')}</th>
            <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_interest')}</th>
            <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_principal')}</th>
            <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_insurance')}</th>
            <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_balance')}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.dueDate} className="border-t">
              <td className="px-2 py-1"><LocalDate value={r.dueDate} /></td>
              <td className={num}>{m(r.payment)}</td>
              <td className={num}>{m(r.interest)}</td>
              <td className={num}>{m(r.principal)}</td>
              <td className={num}>{r.insurance ? m(r.insurance) : null}</td>
              <td className={num}>{m(r.crd)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
