import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { IncomeSearch, RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { PageHeader } from '@/components/page/PageHeader';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { MonthsChart } from './overview/MonthsChart';
import { OverviewFigures } from './overview/OverviewFigures';
import { OverviewFilters } from './overview/OverviewFilters';
import { SourcesTable } from './overview/SourcesTable';
import { SplitBar } from './overview/SplitBar';
import { useOverview } from './overview/useOverview';

/** The income dashboard: filters, key figures, split, months, then the sources (overview design). */
export function IncomeSourcesPage() {
  const { t } = useTranslation();
  const search: RootSearch & IncomeSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const ended = search.ended === 1;
  const { data, error } = useOverview();
  // An empty list without the ended sources asks once more with them, to tell « nothing yet » from « nothing running » (spec §10.4).
  const withEnded = useOverview({ ended: true, enabled: !ended && data?.sources.length === 0 });
  const setEnded = (checked: boolean) =>
    void navigate({ from: '/income', to: '/income', search: (prev: RootSearch & IncomeSearch) => ({ ...prev, ended: checked ? (1 as const) : undefined }), replace: true });
  const select = (source?: string) =>
    void navigate({ from: '/income', to: '/income', replace: true, search: (prev: RootSearch & IncomeSearch) => ({ ...prev, source }) });
  const period = data?.period as '12m' | 'year' | '3y' | undefined;
  const filtered = Boolean(search.person || search.kind || search.source);
  // A brand-new household (no row, no share, no ended source) gets the invitation alone, without 0,00 € figures (spec §4).
  const blank = !filtered && data?.sources.length === 0 && data.split.length === 0 && !withEnded.data?.sources.length;

  // One stable frame whatever the answer: the filters and the switch must survive the reload their own change causes (focus).
  return (
    <div className="space-y-4">
      <PageHeader title={t('income.title')} actions={<Link to="/income/new" className={buttonVariants()}>{t('income.add')}</Link>} />
      <OverviewFilters />
      {data && period && !blank ? (
        <>
          <OverviewFigures figures={data.figures} missingRates={data.missingRates} period={period} year={Number(data.to.slice(0, 4))} />
          <SplitBar split={data.split} missingRates={data.missingRates} selected={search.source} onSelect={select} />
          <MonthsChart months={data.months} missingRates={data.missingRates} period={period} year={Number(data.to.slice(0, 4))} />
        </>
      ) : null}
      <SwitchField label={t('income.show_ended')} checked={ended} onCheckedChange={setEnded} />
      {error ? <ProblemMessage problem={present(toProblem(error))} />
        : <Sources data={data} filtered={filtered} withEnded={ended ? null : withEnded.data} deciding={!ended && data?.sources.length === 0 && !withEnded.data && !withEnded.error} />}
    </div>
  );
}

type Overview = ReturnType<typeof useOverview>['data'];

function Sources({ data, filtered, withEnded, deciding }: { data: Overview; filtered: boolean; withEnded: Overview | null; deciding: boolean }) {
  const { t } = useTranslation();
  if (!data || deciding)
    return (
      <ul aria-busy="true" aria-label={t('income.loading')} className="space-y-2">
        {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
      </ul>
    );

  // Empty: no row and no share, ended ones included.
  const nothingRunning = data.sources.length === 0 && (withEnded?.sources.length ?? 0) > 0;
  if (nothingRunning)
    return (
      <EmptyState title={t('income.none_running_title')} body={t('income.none_running_body')}
        action={<Link from="/income" to="/income" search={(prev: RootSearch & IncomeSearch) => ({ ...prev, ended: 1 as const })} className={buttonVariants({ variant: 'outline' })}>{t('income.see_ended')}</Link>} />
    );
  if (data.sources.length === 0 && data.split.length === 0 && filtered)
    return (
      <EmptyState title={t('income.empty_filter_title')} body={t('income.empty_filter_body')}
        action={(
          <Link from="/income" to="/income" className={buttonVariants({ variant: 'outline' })}
            search={(prev: RootSearch & IncomeSearch) => ({ ...prev, person: undefined, kind: undefined, source: undefined })}>
            {t('income.clear_filters')}
          </Link>
        )} />
    );
  if (data.sources.length === 0 && data.split.length === 0)
    return (
      <EmptyState title={t('income.empty_title')} body={t('income.empty_body')}
        action={<Link to="/income/new" className={buttonVariants()}>{t('income.add')}</Link>} />
    );

  return (
    <>
      <p className="text-sm text-muted-foreground">{t('income.smoothed')}</p>
      <SourcesTable rows={data.sources} />
    </>
  );
}
