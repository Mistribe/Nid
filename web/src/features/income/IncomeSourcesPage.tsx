import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { IncomeSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { PageHeader } from '@/components/page/PageHeader';
import { SectionCard } from '@/components/page/SectionCard';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { MonthsChart } from './overview/MonthsChart';
import { OverviewFigures } from './overview/OverviewFigures';
import { OverviewFilters } from './overview/OverviewFilters';
import { SourcesTable } from './overview/SourcesTable';
import { SplitBar } from './overview/SplitBar';
import { useOverview } from './overview/useOverview';
import { Loading, TableSkeleton } from '@/components/page/skeletons';

/** The income dashboard (overview grammar): filters, hero and key figures, then the split, the months and the sources in their cards. */
export function IncomeSourcesPage() {
  const { t } = useTranslation();
  const search: RootSearch & IncomeSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const ended = search.ended === 1;
  const { data, error } = useOverview();
  // An empty list without the ended sources asks once more with them, to tell « nothing yet » from « nothing running » (spec §10.4).
  const withEnded = useOverview({ ended: true, enabled: !ended && data?.sources.length === 0 });
  const setEnded = (checked: boolean) =>
    void navigate({ from: '/income', to: '/income', search: (prev: RootSearch & IncomeSearch) => ({ ...prev, ended: checked ? (1 as const) : undefined }), replace: true, resetScroll: false });
  const select = (source?: string) =>
    void navigate({ from: '/income', to: '/income', replace: true, resetScroll: false, search: (prev: RootSearch & IncomeSearch) => ({ ...prev, source }) });
  const period = data?.period as '12m' | 'year' | '3y' | undefined;
  const filtered = Boolean(search.person || search.kind || search.source);
  // A brand-new household (no row, no share, no ended source) gets the invitation alone, without 0,00 € figures (spec §4).
  const blank = !filtered && data?.sources.length === 0 && data.split.length === 0 && !withEnded.data?.sources.length;
  const year = Number(data?.to.slice(0, 4));

  // One stable frame whatever the answer: the filters and the switch must survive the reload their own change causes (focus).
  return (
    <div className="space-y-4">
      <PageHeader title={t('income.title')} actions={<Link to="/income/new" className={buttonVariants()}>{t('income.add')}</Link>} />
      <OverviewFilters />
      {data && period && !blank ? (
        <>
          <OverviewFigures figures={data.figures} months={data.months} missingRates={data.missingRates} period={period} year={year} />
          <SplitBar split={data.split} missingRates={data.missingRates} selected={search.source} onSelect={select}
            note={t(`income.overview.split_note_${period}`, { year })} />
          <MonthsChart months={data.months} missingRates={data.missingRates} period={period} year={year} />
        </>
      ) : null}
      <SectionCard title={t('income.overview.sources')} actions={<SwitchField label={t('income.show_ended')} checked={ended} onCheckedChange={setEnded} />}>
        {error ? <ProblemMessage problem={present(toProblem(error))} />
          : <Sources data={data} filtered={filtered} withEnded={ended ? null : withEnded.data} deciding={!ended && data?.sources.length === 0 && !withEnded.data && !withEnded.error} />}
      </SectionCard>
    </div>
  );
}

type Overview = ReturnType<typeof useOverview>['data'];

function Sources({ data, filtered, withEnded, deciding }: { data: Overview; filtered: boolean; withEnded: Overview | null; deciding: boolean }) {
  const { t } = useTranslation();
  if (!data || deciding)
    return (
      <Loading label={t('income.loading')}><TableSkeleton columns={5} /></Loading>
    );

  // Empty: no row and no share, ended ones included.
  const nothingRunning = data.sources.length === 0 && (withEnded?.sources.length ?? 0) > 0;
  if (nothingRunning)
    return (
      <EmptyState module="/income" title={t('income.none_running_title')} body={t('income.none_running_body')}
        action={<Link from="/income" to="/income" search={(prev: RootSearch & IncomeSearch) => ({ ...prev, ended: 1 as const })} className={buttonVariants({ variant: 'outline' })}>{t('income.see_ended')}</Link>} />
    );
  if (data.sources.length === 0 && data.split.length === 0 && filtered)
    return (
      <EmptyState module="/income" title={t('income.empty_filter_title')} body={t('income.empty_filter_body')}
        action={(
          <Link from="/income" to="/income" className={buttonVariants({ variant: 'outline' })}
            search={(prev: RootSearch & IncomeSearch) => ({ ...prev, person: undefined, kind: undefined, source: undefined })}>
            {t('income.clear_filters')}
          </Link>
        )} />
    );
  if (data.sources.length === 0 && data.split.length === 0)
    return (
      <EmptyState module="/income" title={t('income.empty_title')} body={t('income.empty_body')}
        action={<Link to="/income/new" className={buttonVariants({ variant: 'outline' })}>{t('income.add')}</Link>} />
    );

  return (
    <div className="space-y-2">
      <p className="text-sm text-muted-foreground">{t('income.smoothed')}</p>
      <SourcesTable rows={data.sources} />
    </div>
  );
}
