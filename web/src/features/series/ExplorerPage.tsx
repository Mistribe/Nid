import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { useApi } from '@/api/ApiProvider';
import { SERIES_POINTS } from '@/api/series';
import type { SeriesSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { yearsAgoIso } from '@/lib/today';
import { PageHeader } from '@/components/page/PageHeader';
import { SeriesView } from '@/components/page/SeriesView';
import { defaultMode } from '@/components/page/seriesRows';
import { TableToggle } from '@/components/page/TableToggle';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Skeleton } from '@/components/ui/skeleton';
import '@/locales/series';

/** The series named in `?s=` over the last five years (the period picker comes next), as a chart or a table. */
export function ExplorerPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const { s, view }: SeriesSearch = useSearch({ strict: false });
  const { scope }: RootSearch = useSearch({ strict: false });
  const answer = $api.useQuery('get', SERIES_POINTS, { params: { query: { s: s ?? '', from: yearsAgoIso(5), scope: scope === 'me' ? 'me' : 'household' } } }, { enabled: !!s });
  const asTable = view === 'table';
  const toggle = () => void navigate({ to: '.', replace: true, search: ((prev: SeriesSearch) => ({ ...prev, view: asTable ? undefined : 'table' })) as never });
  const data = answer.data;
  return (
    <div className="space-y-4">
      <PageHeader crumbs={[{ label: t('nav.dashboard'), to: '/dashboard' }]} title={t('series.title')}
        actions={data?.series.some((x) => x.points.length > 0) ? <TableToggle asTable={asTable} onToggle={toggle} /> : null} />
      {!s ? <p className="text-sm text-muted-foreground">{t('series.none_selected')}</p>
        : answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : data ? <SeriesView answer={data} mode={defaultMode(data.series.map((x) => x.unit))} asTable={asTable} caption={t('series.chart_table')} />
        : <Skeleton className="h-72 w-full" />}
    </div>
  );
}
