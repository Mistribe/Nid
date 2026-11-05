import { keepPreviousData } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { AlertTriangle, Printer } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { Segmented } from '@/components/form/Segmented';
import { Money } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { downloadBlob } from '../export/download';
import { SUMMARY } from './api';
import { childcareYearCsv, yearLines } from './childcareYearCsv';
import { monthTitle } from './statementText';
import '@/locales/childcare';

/**
 * #411 « Récapitulatif {année} »: the year's validated months summed by the server, as the tax estimate counts them, with
 * the 2042 box of the retained cost and the months left out. The year just ended unless `?year=` says otherwise.
 */
export function YearSummary({ id }: { id: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: { year?: string } = useSearch({ strict: false });
  const navigate = useNavigate();
  const yearLabel = useId();
  const answer = $api.useQuery('get', SUMMARY, { params: { path: { id }, query: search.year ? { year: Number(search.year) } : {} } },
    { placeholderData: keepPreviousData });
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const view = answer.data;
  if (!view) return <div aria-busy="true" aria-label={t('childcare_year.loading')} className="h-40 shimmer rounded-md border bg-muted" />;

  const year = String(view.year);
  const setYear = (next: string) => {
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: Record<string, unknown>) => ({ ...prev, year: next })) as never });
  };
  const years = [...new Set([...view.years.map(String), year])].sort().reverse().map((y) => ({ value: y, label: y }));
  const csv = () => downloadBlob(new Blob([childcareYearCsv(view, t, monthTitle)], { type: 'text/csv' }), `recapitulatif-garde-${year}.csv`);

  return (
    <Section title={t('childcare_year.title', { year })}
      actions={(
        <div className="flex flex-wrap items-center gap-2 print:hidden">
          {years.length > 1 ? (
            <>
              <span id={yearLabel} className="sr-only">{t('childcare_year.year')}</span>
              <Segmented aria-labelledby={yearLabel} value={year} onValueChange={setYear} options={years} />
            </>
          ) : null}
          <Button type="button" variant="outline" onClick={csv}>{t('childcare_year.csv')}</Button>
          <Button type="button" variant="outline" onClick={() => window.print()}><Printer aria-hidden="true" />{t('childcare_year.print')}</Button>
        </div>
      )}>
      <p className="text-sm text-muted-foreground">
        {t('childcare_year.intro', { year })}{' '}
        <Link to="/tax" search={{ year }} className="underline underline-offset-2 print:hidden">{t('childcare_year.to_tax')}</Link>
      </p>
      {view.notValidated.length ? (
        <p className="flex gap-2 rounded-md border border-status-warning/60 bg-status-warning/10 p-3 text-sm">
          <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-status-warning" />
          {t('childcare_year.not_validated', { count: view.notValidated.length, months: view.notValidated.map(monthTitle).join(', ') })}
        </p>
      ) : null}
      {view.validatedMonths.length ? null : <p className="text-sm">{t('childcare_year.none_validated', { year })}</p>}
      <dl className="grid grid-cols-[1fr_auto] gap-x-4 text-sm">
        {yearLines(t, view).map((l) => {
          const retained = l.key === 'retained';
          return (
            <div key={l.key} className="col-span-2 grid grid-cols-subgrid items-baseline border-b py-1.5 last:border-b-0">
              <dt>
                <span className={retained ? 'font-medium' : undefined}>{l.label}</span>
                <span className="block text-xs text-muted-foreground">{l.hint}</span>
                {retained ? <span className="block text-xs text-muted-foreground">{view.box ? t('childcare_year.box', { box: view.box }) : t('childcare_year.box_none')}</span> : null}
              </dt>
              <dd className={retained ? 'text-right font-semibold' : 'text-right'}>{l.amount ? <Money value={l.amount} tabular /> : l.text}</dd>
            </div>
          );
        })}
      </dl>
    </Section>
  );
}
