import { keepPreviousData } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem, type Problem } from '@/api/errors';
import { useApi } from '@/api/ApiProvider';
import { SERIES_POINTS } from '@/api/series';
import type { SeriesSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { Segmented } from '@/components/form/Segmented';
import { PageHeader } from '@/components/page/PageHeader';
import { SeriesView } from '@/components/page/SeriesView';
import { defaultMode, hasModeSwitch } from '@/components/page/seriesRows';
import { TableToggle } from '@/components/page/TableToggle';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Skeleton } from '@/components/ui/skeleton';
import { SeriesPicker } from './SeriesPicker';
import { fromOf, joinKeys, keysOf } from './selection';
import '@/locales/series';

const PERIODS = ['1y', '5y', '10y', 'all'] as const;

/** The 404 names the unknown key as an extension member of the Problem. */
type NotFound = Problem & { key?: string };

/** The series named in `?s=` over the period chosen, as a chart or a table; chips and a catalogue edit the selection. */
export function ExplorerPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const { s, view, p, axes }: SeriesSearch = useSearch({ strict: false });
  const { scope }: RootSearch = useSearch({ strict: false });
  const [dropped, setDropped] = useState(false);
  const keys = keysOf(s);
  const answer = $api.useQuery('get', SERIES_POINTS, { params: { query: { s: s ?? '', from: fromOf(p), scope: scope === 'me' ? 'me' : 'household' } } },
    { enabled: !!s, placeholderData: keepPreviousData });
  const asTable = view === 'table';
  const toggle = () => void navigate({ to: '/series', replace: true, search: (prev) => ({ ...prev, view: asTable ? undefined : ('table' as const) }) });
  const set = (patch: Partial<SeriesSearch>) => void navigate({ to: '/series', replace: true, search: (prev) => ({ ...prev, view: prev.view === 'table' ? ('table' as const) : undefined, ...patch }) });
  const edit = (next: string[]) => set({ s: joinKeys(next) });
  const onAdd = (k: string) => { if (!keys.includes(k)) { setDropped(false); edit([...keys, k]); } };
  const onRemove = (k: string) => { setDropped(false); edit(keys.filter((x) => x !== k)); };
  // A disabled query would otherwise keep the old answer.
  const data = s ? answer.data : undefined;
  const series = data?.series ?? [];
  const problem = s && answer.error ? (toProblem(answer.error) as NotFound) : null;
  const missing = problem?.code === 'core.series_not_found' ? problem.key : undefined;
  useEffect(() => {
    if (missing && keys.includes(missing)) { setDropped(true); edit(keys.filter((x) => x !== missing)); }
  // eslint-disable-next-line react-hooks/exhaustive-deps -- once per answer; `keys`/`edit` change with it
  }, [missing]);
  const units = series.map((x) => x.unit);
  const switchable = hasModeSwitch(units);
  const mode = axes === '2' && switchable ? 'axes2' : defaultMode(units);
  return (
    <div className="space-y-4">
      <PageHeader crumbs={[{ label: t('nav.dashboard'), to: '/dashboard' }]} title={t('series.title')} crumbLabel={t('series.crumb')}
        actions={series.some((x) => x.points.length > 0) ? <TableToggle asTable={asTable} onToggle={toggle} /> : null} />
      <SeriesPicker keys={keys} series={series} mode={mode} focus={!s} onAdd={onAdd} onRemove={onRemove} />
      <div className="flex flex-wrap gap-3">
        <Segmented aria-label={t('series.period')} value={p ?? '5y'} onValueChange={(v) => set({ p: v === '5y' ? undefined : (v as SeriesSearch['p']) })}
          options={PERIODS.map((v) => ({ value: v, label: t(`series.period_${v}`) }))} />
        {switchable ? (
          <Segmented aria-label={t('series.scale')} value={mode === 'axes2' ? '2' : '100'} onValueChange={(v) => set({ axes: v === '2' ? '2' : undefined })}
            options={[{ value: '100', label: t('series.base100') }, { value: '2', label: t('series.axes2') }]} />
        ) : null}
      </div>
      {dropped ? <p role="status" className="text-sm text-muted-foreground">{t('series.dropped')}</p> : null}
      {!s ? <p className="text-sm text-muted-foreground">{t('series.invite')}</p>
        : missing ? null
        : problem ? <ProblemMessage problem={present(problem)} />
        : data ? <SeriesView answer={data} mode={mode} asTable={asTable} caption={t('series.chart_table')} legend={false} />
        : <Skeleton className="h-72 w-full" />}
    </div>
  );
}
