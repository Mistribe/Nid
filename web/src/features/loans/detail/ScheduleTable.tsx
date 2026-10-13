import { ChevronDown, ChevronRight } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import type { LoanSchedule } from '../api';
import { money } from '../format';
import { monthsOf } from '../schedule';

const th = 'px-2 py-1 font-medium';
const num = 'px-2 py-1 text-right tabular-nums';

/**
 * Spec 3B1 §7: one line per year — the server's totals and the balance on 31 December — each a button with
 * `aria-expanded` folding out its months; only open years render their months (precision 8). The marked payment is the
 * figures' next one and its year is the one open (3B2 design §4.1–4.2), marked by words, never by colour alone.
 */
export function ScheduleTable({ schedule, currency, nextPaymentOn }: { schedule: LoanSchedule; currency: string; nextPaymentOn?: string | null }) {
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
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <caption className="sr-only">{t('loan_detail.schedule.table')}</caption>
          <thead>
            <tr>
              <th scope="col" className={`${th} text-left`}>{t('loan_detail.schedule.col_number')}</th>
              <th scope="col" className={`${th} text-left`}>{t('loan_detail.schedule.col_date')}</th>
              <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_payment')}</th>
              <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_interest')}</th>
              <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_principal')}</th>
              <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_insurance')}</th>
              <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_early')}</th>
              <th scope="col" className={`${th} text-right`}>{t('loan_detail.schedule.col_balance')}</th>
            </tr>
          </thead>
          {schedule.years.map((y) => {
            const isOpen = open.has(y.year);
            return (
              <tbody key={y.year} className="border-t">
                <tr className="font-medium">
                  <th scope="row" colSpan={2} className="px-2 py-1 text-left">
                    <button type="button" aria-expanded={isOpen} onClick={() => toggle(y.year)} className="inline-flex items-center gap-1 hover:underline">
                      {isOpen ? <ChevronDown aria-hidden="true" className="size-4" /> : <ChevronRight aria-hidden="true" className="size-4" />}
                      {t('loan_detail.schedule.year', { year: y.year })}
                    </button>
                  </th>
                  <td className={num}>{m(y.payment)}</td>
                  <td className={num}>{m(y.interest)}</td>
                  <td className={num}>{m(y.principal)}</td>
                  <td className={num}>{m(y.insurance)}</td>
                  <td className={num}>{m(y.earlyRepayment)}</td>
                  <td className={num}>{m(y.balance)}</td>
                </tr>
                {isOpen ? monthsOf(schedule.rows, y.year).map((r) => (
                  <tr key={r.number} className={r === next ? 'bg-muted' : undefined}>
                    <td className="px-2 py-1 tabular-nums">{r.number}</td>
                    <td className="px-2 py-1">
                      <LocalDate value={r.dueDate} />
                      {r === next ? <span className="ml-2 rounded border px-1 text-xs">{t('loan_detail.schedule.next')}</span> : null}
                    </td>
                    <td className={num}>{m(r.payment)}</td>
                    <td className={num}>{m(r.interest)}</td>
                    <td className={num}>{m(r.principal)}</td>
                    <td className={num}>{m(r.insurance)}</td>
                    <td className={num}>{r.earlyRepayment ? m(r.earlyRepayment) : null}</td>
                    <td className={num}>{m(r.balance)}</td>
                  </tr>
                )) : null}
              </tbody>
            );
          })}
        </table>
      </div>
    </div>
  );
}
