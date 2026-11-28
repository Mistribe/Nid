import type { FieldValues, Path, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { Toggle } from '@/components/ui/toggle';
import { cn } from '@/lib/utils';
import type { GapRow } from './childcareSchema';
import { monthsOf, mondaysOf, plusDays, rowsOf, type GapYear } from './gapWeeks';
import '@/locales/childcare';

type WithGaps = FieldValues & { gaps: GapRow[] };

const monthShort = new Intl.DateTimeFormat('fr-FR', { timeZone: 'UTC', month: 'short' });

/**
 * The weeks without care as one calendar per contract year: a cell per week (its Monday), pressed when the child is not cared for.
 * The rows sent stay a first Monday and a number of weeks, one per run; the server checks each year against its programmed weeks.
 */
export function GapFields<T extends WithGaps>({ form, years }: { form: UseFormReturn<T>; years: GapYear[] }) {
  const { t } = useTranslation();
  const name = 'gaps' as Path<T>;
  const rows = form.watch(name) as GapRow[];
  const error = (form.formState.errors.gaps as { message?: string } | undefined)?.message;
  // A full year has none: its leave goes in the months' days.
  if (form.watch('fullYear' as Path<T>) === true) return <p className="text-sm text-muted-foreground">{t('childcare_form.gaps_full_year')}</p>;
  const placed = mondaysOf(rows);
  const toggle = (monday: string) => {
    const next = new Set(placed);
    if (!next.delete(monday)) next.add(monday);
    form.setValue(name, rowsOf(next) as never, { shouldDirty: true });
  };
  return (
    <div className="space-y-6">
      <p className="text-sm text-muted-foreground">{t('childcare_form.gaps_hint')}</p>
      {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
      {years.length === 0 ? <p className="text-sm text-muted-foreground">{t('childcare_form.gaps_no_start')}</p> : null}
      {years.map((y) => <YearWeeks key={y.from} year={y} placed={placed} onToggle={toggle} />)}
    </div>
  );
}

function YearWeeks({ year, placed, onToggle }: { year: GapYear; placed: Set<string>; onToggle: (monday: string) => void }) {
  const { t } = useTranslation();
  const label = t('childcare_detail.gap_year', { from: formatLocalDate(year.from, 'short'), to: formatLocalDate(year.to, 'short') });
  const inYear = [...placed].filter((m) => m >= year.from && m <= year.to);
  const left = year.allowed - inYear.length;
  return (
    <section aria-label={label} className="space-y-2">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h3 className="text-sm font-medium">{label}</h3>
        <span className={cn('rounded-full px-2 py-0.5 text-xs font-medium',
          left === 0 ? 'bg-status-good/15 text-status-good-text' : left > 0 ? 'bg-status-warning/15 text-status-warning-text' : 'bg-destructive/10 text-destructive')}>
          {t('childcare_form.gaps_count', { placed: inYear.length, allowed: year.allowed })}
          {left > 0 ? ` · ${t('childcare_form.gaps_left', { count: left })}` : left < 0 ? ` · ${t('childcare_form.gaps_over', { count: -left })}` : ''}
        </span>
      </div>
      <div className="grid grid-cols-[repeat(auto-fill,minmax(2.5rem,1fr))] gap-1">
        {monthsOf(year).map(({ month, mondays }) => (
          <div key={month} className="flex flex-col gap-1">
            <span aria-hidden="true" className="text-center text-xs text-muted-foreground">{monthShort.format(new Date(`${month}-01T00:00:00Z`))}</span>
            {mondays.map((m) => (
              <Toggle key={m} variant="outline" size="sm" pressed={placed.has(m)} onPressedChange={() => onToggle(m)}
                aria-label={t('childcare_form.gap_week', { date: formatLocalDate(m) })}
                className="w-full data-[state=on]:border-primary data-[state=on]:bg-primary data-[state=on]:text-primary-foreground">
                {Number(m.slice(8))}
              </Toggle>
            ))}
          </div>
        ))}
      </div>
      {inYear.length > 0 ? (
        <p className="text-xs text-muted-foreground">
          {rowsOf(inYear).map((r) => `${formatLocalDate(r.monday, 'short')} → ${formatLocalDate(plusDays(r.monday, 7 * Number(r.weeks) - 1), 'short')} · ${t('childcare_detail.weeks', { count: Number(r.weeks) })}`).join(' ; ')}
        </p>
      ) : null}
    </section>
  );
}
