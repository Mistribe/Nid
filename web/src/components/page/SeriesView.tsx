import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { CartesianGrid, Line, LineChart, ReferenceArea, ReferenceLine, XAxis, YAxis } from 'recharts';
import type { SeriesPoints, SeriesPointsItem } from '@/api/series';
import { formatLocalDate } from '@/components/LocalDate';
import { SortableTable } from '@/components/page/SortableTable';
import { TableToggle } from '@/components/page/TableToggle';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { todayIso } from '@/lib/today';
import { formatSeriesValue, seriesLabel, unitLabel } from './seriesFormat';
import { axisOf, drawable, pointsAt, seriesRows, type SeriesMode, type SeriesRow } from './seriesRows';
import '@/locales/series';

// Literal classes, so Tailwind sees them: the chart palette in order (dataviz.md §3).
const CHIPS = ['bg-[var(--chart-1)]', 'bg-[var(--chart-2)]', 'bg-[var(--chart-3)]', 'bg-[var(--chart-4)]', 'bg-[var(--chart-5)]', 'bg-[var(--chart-6)]'];
const monthYear = new Intl.DateTimeFormat('fr-FR', { timeZone: 'UTC', month: 'short', year: 'numeric' });
const chip = (i: number) => CHIPS[i % CHIPS.length];
const colour = (i: number) => `var(--chart-${(i % 6) + 1})`;

const Chip = ({ i }: { i: number }) => <span aria-hidden="true" className={`inline-block size-2.5 shrink-0 rounded-full ${chip(i)}`} />;

/** One or several series of the explorer: a chart, or the same values as a table. Every figure read is the server's string. */
export function SeriesView({ answer, mode, asTable, onToggle, caption }: {
  answer: SeriesPoints; mode: SeriesMode; asTable: boolean; onToggle?: () => void; caption: string;
}) {
  const { t } = useTranslation();
  const { series } = answer;
  if (series.every((s) => s.points.length === 0)) return <p className="text-sm text-muted-foreground">{t('series.empty')}</p>;

  const rows = seriesRows(series, mode);
  const axes = axisOf(series, mode);
  const first = (side: 'left' | 'right') => series.find((_, i) => axes[i] === side);
  const unitOn = (side: 'left' | 'right') => { const f = first(side); return f ? unitLabel(t, f.unit) : null; };
  const table = <ValuesTable series={series} rows={rows} mode={mode} caption={caption} />;
  return (
    <div className="space-y-3">
      {onToggle ? <div className="flex justify-end"><TableToggle asTable={asTable} onToggle={onToggle} /></div> : null}
      {mode === 'base100' && answer.baseDate ? <p className="text-sm text-muted-foreground">{t('series.base_date', { date: formatLocalDate(answer.baseDate, 'short') })}</p> : null}
      {mode === 'axes2' && !asTable ? (
        <div className="flex justify-between gap-4 text-sm text-muted-foreground">
          {(['left', 'right'] as const).map((side) => (
            <span key={side} className="inline-flex flex-wrap items-center gap-1.5">
              {unitOn(side)}
              {series.map((s, i) => (axes[i] === side ? <Chip key={s.key} i={i} /> : null))}
            </span>
          ))}
        </div>
      ) : null}
      {series.length >= 1 ? (
        <ul aria-label={t('series.legend')} className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
          {series.map((s, i) => (
            <li key={s.key} className="inline-flex items-center gap-1.5">
              <Chip i={i} />{seriesLabel(t, s)}
              {drawable(s, mode) ? null : <span className="text-muted-foreground">({t('series.no_base')})</span>}
            </li>
          ))}
        </ul>
      ) : null}
      {asTable ? table : (
        <>
          <Chart series={series} rows={rows} axes={axes} mode={mode} />
          <div className="sr-only">{table}</div>
        </>
      )}
    </div>
  );
}

/** 1 January of each year inside the data's range, so no year label repeats; years only, no amount. */
function yearStarts(rows: SeriesRow[]): number[] {
  const first = rows[0]?.date.slice(0, 4), last = rows.at(-1)?.date.slice(0, 4);
  if (!first || !last) return [];
  const ticks: number[] = [];
  for (let y = Number(first); y <= Number(last); y += 1) {
    const at = Date.parse(`${y}-01-01`);
    if (at >= (rows[0]?.at ?? 0)) ticks.push(at);
  }
  return ticks;
}

/** The year starts; when the range holds none (inside one year), its first and last dates. */
const axisTicks = (rows: SeriesRow[]): number[] => {
  const years = yearStarts(rows);
  return years.length ? years : [...new Set([rows[0]?.at, rows.at(-1)?.at])].filter((n): n is number => n != null);
};

function Chart({ series, rows, axes, mode }: { series: SeriesPoints['series']; rows: SeriesRow[]; axes: ('left' | 'right')[]; mode: SeriesMode }) {
  const { t } = useTranslation();
  const config = Object.fromEntries(series.map((s, i) => [`s${i}`, { label: seriesLabel(t, s), color: colour(i) }])) satisfies ChartConfig;
  const today = todayIso();
  const last = rows.at(-1)?.date ?? '';
  const compact = new Intl.NumberFormat('fr-FR', { notation: 'compact', maximumFractionDigits: 2 });
  const axis = (n: number) => compact.format(n);
  return (
    <ChartContainer config={config} className="h-72 w-full" aria-hidden="true">
      <LineChart data={rows} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="at" type="number" scale="time" domain={['dataMin', 'dataMax']} ticks={axisTicks(rows)} tickLine={false} axisLine={false}
          tickFormatter={(n: number) => (yearStarts(rows).length ? String(new Date(n).getUTCFullYear()) : monthYear.format(n))} />
        <YAxis yAxisId="left" width={56} tickLine={false} axisLine={false} domain={['auto', 'auto']} tickFormatter={axis} />
        {mode === 'axes2' ? <YAxis yAxisId="right" orientation="right" width={56} tickLine={false} axisLine={false} domain={['auto', 'auto']} tickFormatter={axis} /> : null}
        {last > today ? <ReferenceArea yAxisId="left" x1={Date.parse(today)} x2={Date.parse(last)} fill="var(--muted)" fillOpacity={0.5} /> : null}
        <ReferenceLine yAxisId="left" x={Date.parse(today)} stroke="var(--border)" strokeDasharray="4 3" label={{ value: t('series.today'), position: 'insideTopLeft', fontSize: 11 }} />
        <ChartTooltip content={<Tip rows={rows} series={series} mode={mode} t={t} />} />
        {series.map((s, i) => (drawable(s, mode) ? (
          <Line key={s.key} dataKey={(r: SeriesRow) => r.plot[i]} yAxisId={axes[i]} stroke={`var(--color-s${i})`} strokeWidth={2}
            type={s.frequency === 'daily' || s.frequency === 'monthly' ? 'monotone' : 'stepAfter'} connectNulls animationDuration={800}
            dot={s.points.length === 1 ? { r: 4, fill: `var(--color-s${i})` } : false} />
        ) : null))}
      </LineChart>
    </ChartContainer>
  );
}

/** Reads the server's strings from the row, never the plotted numbers. */
function Tip({ active, payload, rows, series, mode, t }: {
  active?: boolean; payload?: { payload?: SeriesRow }[]; rows: SeriesRow[]; series: SeriesPoints['series']; mode: SeriesMode; t: TFunction;
}) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  const at = pointsAt(rows, rows.indexOf(row));
  return (
    <div className="grid gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <span className="font-medium">{formatLocalDate(row.date, 'short')}</span>
      {series.map((s, i) => {
        const p = at[i];
        return p ? (
          <span key={s.key} className="flex items-center gap-1.5">
            <Chip i={i} />{seriesLabel(t, s)}
            <span className="ml-auto pl-3 tabular-nums">
              {formatSeriesValue(s.unit, p.value)}
              {mode === 'base100' && p.base100 != null ? ` · ${formatSeriesValue('index', p.base100)}` : null}
            </span>
          </span>
        ) : null;
      })}
    </div>
  );
}

function ValuesTable({ series, rows, mode, caption }: { series: SeriesPoints['series']; rows: SeriesRow[]; mode: SeriesMode; caption: string }) {
  const { t } = useTranslation();
  const rebased = (s: SeriesPointsItem) => mode === 'base100' && drawable(s, mode);
  return (
    <SortableTable<SeriesRow> caption={caption} rows={rows} rowKey={(r) => r.date} initialSort={{ id: 'date', desc: true }}
      columns={[
        { id: 'date', header: t('series.date'), sort: (r) => r.date, cell: (r) => formatLocalDate(r.date, 'short') },
        ...series.flatMap((s, i) => [
          { id: `v${i}`, header: `${seriesLabel(t, s)} (${unitLabel(t, s.unit)})`, align: 'end' as const,
            cell: (r: SeriesRow) => { const p = r.points[i]; return p ? <span className="tabular-nums">{formatSeriesValue(s.unit, p.value)}</span> : ''; } },
          ...(rebased(s) ? [{ id: `b${i}`, header: t('series.base100_column', { label: seriesLabel(t, s) }), align: 'end' as const,
            cell: (r: SeriesRow) => { const b = r.points[i]?.base100; return b == null ? '' : <span className="tabular-nums">{formatSeriesValue('index', b)}</span>; } }] : []),
        ]),
      ]}
      mobile={(r) => (
        <div className="space-y-1 text-sm">
          <div className="font-medium">{formatLocalDate(r.date, 'short')}</div>
          {series.map((s, i) => { const p = r.points[i]; return p ? <div key={s.key} className="flex justify-between gap-2"><span className="text-muted-foreground">{seriesLabel(t, s)}</span><span className="tabular-nums">{formatSeriesValue(s.unit, p.value)}{rebased(s) && p.base100 != null ? ` · ${formatSeriesValue('index', p.base100)}` : null}</span></div> : null; })}
        </div>
      )} />
  );
}
