import { useNavigate, useSearch } from '@tanstack/react-router';
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { SpendingSearch } from '@/app/router';
import { monthText } from '@/components/LocalDate';
import { Button } from '@/components/ui/button';
import { daysIn, shiftMonth } from '@/features/calendar/calendarDates';
import type { RootSearch } from '@/lib/search';
import { todayIso } from '@/lib/today';
import '@/locales/spending';

/** An arrow at the end stays focusable (`aria-disabled`), as the year stepper's. */
const END = 'aria-disabled:cursor-default aria-disabled:opacity-50 aria-disabled:hover:bg-transparent';

/** The month shown (`?month=`, the current one when absent or not yet past) and the period (`?period=12m`), and how to change them. */
export function useMonth() {
  const navigate = useNavigate();
  const search: RootSearch & SpendingSearch = useSearch({ strict: false });
  const current = todayIso().slice(0, 7);
  const month = search.month && search.month < current ? search.month : current;
  const set = (patch: SpendingSearch) =>
    void navigate({ from: '/spending', to: '/spending', replace: true, resetScroll: false, search: (prev: RootSearch & SpendingSearch) => ({ ...prev, ...patch }) });
  // Another month starts on the Opérations tab's first page.
  const go = (by: number) => { const next = shiftMonth(month, by); set({ month: next >= current ? undefined : next, page: undefined }); };
  return { search, current, month, year: search.period === '12m', set, go };
}

/** A month's first and last days, `yyyy-mm-dd`. */
export function monthDays(month: string) {
  return { from: `${month}-01`, to: `${month}-${daysIn(Number(month.slice(0, 4)), Number(month.slice(5, 7)))}` };
}

/** The month stepper of Dépenses' header: the overview's and the Opérations tab's. */
export function MonthStepper() {
  const { t } = useTranslation();
  const { current, month, go } = useMonth();
  return (
    <div role="group" aria-label={t('spending.month')} data-header-control className="inline-flex items-center rounded-full border border-input">
      <Button type="button" variant="ghost" size="icon" aria-label={t('spending.previous')} onClick={() => go(-1)}>
        <ChevronLeftIcon aria-hidden="true" />
      </Button>
      <span aria-live="polite" className="min-w-32 px-1 text-center tabular-nums first-letter:uppercase">{monthText(month)}</span>
      <Button type="button" variant="ghost" size="icon" aria-label={t('spending.next')} aria-disabled={month === current || undefined} className={END}
        onClick={() => { if (month !== current) go(1); }}>
        <ChevronRightIcon aria-hidden="true" />
      </Button>
    </div>
  );
}
