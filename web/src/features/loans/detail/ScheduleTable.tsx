import { ChevronDown, ChevronRight, Landmark } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { Badge } from '@/components/ui/badge';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { LoanSchedule, LoanScheduleRow } from '../api';
import { money } from '../format';
import { monthsOf } from '../schedule';
import '@/locales/loans';

const num = 'text-right tabular-nums';

/** The fields the table reads: a loan's schedule, or a scenario's (no bank rows there). */
type ScheduleRow = Pick<LoanScheduleRow, 'number' | 'dueDate' | 'payment' | 'interest' | 'principal' | 'insurance' | 'earlyRepayment' | 'balance'> & { fromBank?: boolean };
type Schedule = { rows: ScheduleRow[]; years: LoanSchedule['years'] };

/**
 * Spec 3B1 §7: one line per year — the server's totals and the balance on 31 December — each a button with
 * `aria-expanded` folding out its months; only open years render their months (precision 8). The marked payment is the
 * figures' next one and its year is the one open (3B2 design §4.1–4.2), marked by words, never by colour alone; with the bank as the reference, its rows say « Banque » (3F2).
 */
export function ScheduleTable({ schedule, currency, nextPaymentOn }: { schedule: Schedule; currency: string; nextPaymentOn?: string | null }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState<ReadonlySet<number>>(() => new Set(nextPaymentOn ? [Number(nextPaymentOn.slice(0, 4))] : []));
  // A payment due today counts as paid, as the server's figures say: the marked row is theirs, never the browser's today.
  const next = nextPaymentOn ? schedule.rows.find((r) => r.dueDate === nextPaymentOn) : undefined;
  const m = (amount: string) => money(currency, amount);
  const toggle = (year: number) => setOpen((prev) => {
    const now = new Set(prev);
    if (now.has(year)) now.delete(year); else now.add(year);
    return now;
  });

  return (
    <div className="space-y-2">
      <p className="text-sm text-muted-foreground">{t('loan_detail.schedule.help')}</p>
      <Table aria-label={t('loan_detail.schedule.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('loan_detail.schedule.col_number')}</TableHead>
            <TableHead>{t('loan_detail.schedule.col_date')}</TableHead>
            <TableHead className="text-right">{t('loan_detail.schedule.col_payment')}</TableHead>
            <TableHead className="text-right">{t('loan_detail.schedule.col_interest')}</TableHead>
            <TableHead className="text-right">{t('loan_detail.schedule.col_principal')}</TableHead>
            <TableHead className="text-right">{t('loan_detail.schedule.col_insurance')}</TableHead>
            <TableHead className="text-right">{t('loan_detail.schedule.col_early')}</TableHead>
            <TableHead className="text-right">{t('loan_detail.schedule.col_balance')}</TableHead>
          </TableRow>
        </TableHeader>
        {schedule.years.map((y) => {
          const isOpen = open.has(y.year);
          return (
            <TableBody key={y.year}>
              <TableRow className="font-medium">
                <TableHead scope="row" colSpan={2}>
                  <button type="button" aria-expanded={isOpen} onClick={() => toggle(y.year)} className="inline-flex items-center gap-1 rounded-sm hover:underline focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none">
                    {isOpen ? <ChevronDown aria-hidden="true" className="size-4" /> : <ChevronRight aria-hidden="true" className="size-4" />}
                    {t('loan_detail.schedule.year', { year: y.year })}
                  </button>
                </TableHead>
                <TableCell className={num}>{m(y.payment)}</TableCell>
                <TableCell className={num}>{m(y.interest)}</TableCell>
                <TableCell className={num}>{m(y.principal)}</TableCell>
                <TableCell className={num}>{m(y.insurance)}</TableCell>
                <TableCell className={num}>{m(y.earlyRepayment)}</TableCell>
                <TableCell className={num}>{m(y.balance)}</TableCell>
              </TableRow>
              {isOpen ? monthsOf(schedule.rows, y.year).map((r) => (
                <TableRow key={r.number} className={r === next ? 'bg-muted' : undefined}>
                  <TableCell className="tabular-nums">{r.number}</TableCell>
                  <TableCell>
                    <LocalDate value={r.dueDate} />
                    {r === next ? <Badge variant="outline" className="ml-2">{t('loan_detail.schedule.next')}</Badge> : null}
                    {r.fromBank ? (
                      <Badge variant="outline" className="ml-2"><Landmark aria-hidden="true" />{t('loan_detail.schedule.bank')}</Badge>
                    ) : null}
                  </TableCell>
                  <TableCell className={num}>{m(r.payment)}</TableCell>
                  <TableCell className={num}>{m(r.interest)}</TableCell>
                  <TableCell className={num}>{m(r.principal)}</TableCell>
                  <TableCell className={num}>{m(r.insurance)}</TableCell>
                  <TableCell className={num}>{r.earlyRepayment ? m(r.earlyRepayment) : null}</TableCell>
                  <TableCell className={num}>{m(r.balance)}</TableCell>
                </TableRow>
              )) : null}
            </TableBody>
          );
        })}
      </Table>
    </div>
  );
}
