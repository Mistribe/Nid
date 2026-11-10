import { keepPreviousData } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { SERIES_POINTS } from '@/api/series';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SeriesView } from '@/components/page/SeriesView';
import { defaultMode } from '@/components/page/seriesRows';
import { Loading } from '@/components/page/skeletons';
import { Skeleton } from '@/components/ui/skeleton';
import '@/locales/admin';
import '@/locales/series';

/** A reference series drawn on its admin page: the server's points, as a chart or, on demand, a table. */
export function AdminSeries({ seriesKey, from }: { seriesKey: string; from?: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [asTable, setAsTable] = useState(false);
  const points = $api.useQuery('get', SERIES_POINTS, { params: { query: { s: seriesKey, from } } }, { placeholderData: keepPreviousData });

  if (points.error) return <ProblemMessage problem={present(toProblem(points.error))} />;
  if (!points.data) return <Loading label={t('admin.loading')}><Skeleton className="h-72 w-full" /></Loading>;
  return (
    <SeriesView answer={points.data} mode={defaultMode(points.data.series.map((s) => s.unit))} asTable={asTable}
      onToggle={() => setAsTable((v) => !v)} caption={t('series.chart_table')} />
  );
}
