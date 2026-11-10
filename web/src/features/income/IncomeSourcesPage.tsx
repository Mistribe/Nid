import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { IncomeSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
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

/** The income dashboard (overview grammar): filters, then « Vue d’ensemble » (figures, months) and « Sources » (split, table). */
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
  const [tab, setTab] = useDetailTab(TABS);

  const sources = (
    <SectionCard title={t('income.overview.sources')} actions={<SwitchField label={t('income.show_ended')} checked={ended} onCheckedChange={setEnded} />}>
      {error ? <ProblemMessage problem={present(toProblem(error))} />
        : <Sources data={data} filtered={filtered} withEnded={ended ? null : withEnded.data} deciding={!ended && data?.sources.length === 0 && !withEnded.data && !withEnded.error} />}
    </SectionCard>
  );
  // One stable frame whatever the answer: the filters and the switch must survive the reload their own change causes (focus).
  // Issue #20: the overview is the figures and the months; « Sources » the split and the table. Until there is an answer (or
  // for a brand-new household) both show the sources card, which carries the loading, the error or the invitation.
  return (
    <div className="space-y-4">
      <PageHeader title={t('income.title')} actions={<Link to="/income/new" className={buttonVariants()}>{t('income.add')}</Link>} />
      <OverviewFilters />
      {blank ? sources : (
        <DetailTabs value={tab} onChange={setTab} tabs={[
          { id: 'overview', label: t('income.tab_overview'), content: data && period ? (
            <div className="space-y-4">
              <OverviewFigures figures={data.figures} missingRates={data.missingRates} period={period} year={year} />
              <MonthsChart months={data.months} missingRates={data.missingRates} period={period} year={year} />
            </div>
          ) : sources },
          { id: 'sources', label: t('income.tab_sources'), content: (
            <div className="space-y-4">
              {data && period ? (
                <SplitBar split={data.split} missingRates={data.missingRates} selected={search.source} onSelect={select}
                  note={t(`income.overview.split_note_${period}`, { year })} />
              ) : null}
              {sources}
            </div>
          ) },
        ]} />
      )}
    </div>
  );
}

const TABS = ['overview', 'sources'] as const;

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
