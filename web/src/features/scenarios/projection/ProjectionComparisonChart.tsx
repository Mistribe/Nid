import { useTranslation } from 'react-i18next';
import { Area, CartesianGrid, ComposedChart, Line, ReferenceDot, XAxis, YAxis } from 'recharts';
import { formatCurrency } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { cn } from '@/lib/utils';
import type { HouseholdPoint, ProjectionMilestone, RangePoint } from '@/api/dashboard';
import { bandOf } from '@/features/dashboard/ProjectionRanges';
import { toPlot } from '@/lib/plot';
import type { ChartSeries } from './chartSeries';
import '@/locales/scenarios';

type Row = {
  on: string; plot: Record<string, number>; text: Record<string, string>; marks: string[];
  band?: [number, number]; median?: number; range?: { low: string; median: string; high: string };
};

/** yyyy-mm-dd as jj/mm/aaaa (a string swap, nothing computed). */
const dayFirst = (iso: string) => iso.split('-').reverse().join('/');
const isYearEnd = (p: HouseholdPoint) => p.on.endsWith('-12-31');
const reading = (p: HouseholdPoint, real: boolean) => (real ? p.real : p.nominal);

/** A milestone as the person reads it: its kind and the server's label, as the dashboard's card words it. */
export function useMilestoneText() {
  const { t } = useTranslation();
  return (m: ProjectionMilestone) => (m.kind === 'event'
    ? t('scenarios.projection.result.milestone_event', { label: m.label })
    : t(`dashboard.projection.milestone_${m.kind}`, { label: m.label, defaultValue: m.label }));
}

/**
 * Spec §8, §14.2 and dataviz.md: each series as a line on the same grid, in the fixed chart colours (`--chart-1`, `--chart-2`…), the
 * x axis ticking once a year; `milestones` (the detail page's) are dots on the second line. `toPlot` only places the marks: the tooltip
 * shows the server's strings. Ranges spec §6: `band` (the second line's) is the 10th–90th percentile as a tinted area of its colour,
 * its median dashed.
 */
export function ProjectionComparisonChart({ series, currency, milestones, real, className, band }: {
  series: ChartSeries[]; currency: string; milestones?: ProjectionMilestone[]; real: boolean; className?: string; band?: RangePoint[];
}) {
  const { t } = useTranslation();
  const text = useMilestoneText();
  const config = Object.fromEntries(series.map((s, i) => [s.key, { label: s.label, color: `var(--chart-${i + 1})` }])) satisfies ChartConfig;
  const rows = new Map<string, Row>();
  const row = (on: string) => rows.get(on) ?? rows.set(on, { on, plot: {}, text: {}, marks: [] }).get(on)!;
  for (const s of series) {
    for (const p of s.points) {
      const r = row(p.on);
      r.plot[s.key] = toPlot(reading(p, real));
      r.text[s.key] = formatCurrency(reading(p, real), currency);
    }
  }
  for (const p of band ?? []) {
    const b = bandOf(p, real);
    Object.assign(row(p.on), { band: [toPlot(b.low), toPlot(b.high)], median: toPlot(b.median), range: b });
  }
  const data = [...rows.values()].sort((a, b) => a.on.localeCompare(b.on));
  // A milestone sits on the first point of the second line at or after its date (ISO dates compare as strings).
  const marked = series[1]?.key;
  const dots = marked === undefined ? [] : (milestones ?? []).flatMap((m) => {
    const at = data.find((r) => r.on >= m.on && r.plot[marked] !== undefined);
    if (!at) return [];
    at.marks.push(text(m));
    return [{ key: `${m.kind}-${m.on}-${m.item ?? m.label}`, on: at.on, plot: at.plot[marked]! }];
  });
  const ticks = (series[0]?.points ?? []).filter(isYearEnd).map((p) => p.on);
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  return (
    <div className="space-y-2">
      <ul aria-label={t('scenarios.projection.result.legend')} className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
        {series.map((s) => (
          <li key={s.key} className="flex items-center gap-1.5">
            <span aria-hidden="true" className="h-0.5 w-4 rounded" style={{ background: config[s.key]!.color }} />
            {s.label}
          </li>
        ))}
        {band && marked ? (
          <>
            <li className="flex items-center gap-1.5">
              <span aria-hidden="true" className="w-4 border-t-2 border-dashed" style={{ borderColor: config[marked]!.color }} />{t('dashboard.projection.ranges_median')}
            </li>
            <li className="flex items-center gap-1.5">
              <span aria-hidden="true" className="h-3 w-4 rounded-sm opacity-20" style={{ background: config[marked]!.color }} />{t('dashboard.projection.ranges_band')}
            </li>
          </>
        ) : null}
      </ul>
      <ChartContainer config={config} className={cn('h-56 w-full', className)}>
        <ComposedChart data={data} title={t('scenarios.projection.result.chart_title')} accessibilityLayer>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="on" ticks={ticks} tickFormatter={(on: string) => on.slice(0, 4)} tickLine={false} axisLine={false} tickMargin={8} />
          <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
          <ChartTooltip content={<ComparisonTooltip series={series} currency={currency} />} />
          {band && marked ? <Area dataKey="band" type="monotone" stroke="none" fill={`var(--color-${marked})`} fillOpacity={0.2} isAnimationActive={false} /> : null}
          {band && marked ? <Line dataKey="median" type="monotone" stroke={`var(--color-${marked})`} strokeWidth={1.5} strokeDasharray="4 4" dot={false} activeDot={false} isAnimationActive={false} /> : null}
          {series.map((s) => (
            <Line key={s.key} dataKey={(r: Row) => r.plot[s.key]} name={s.key} type="monotone" stroke={`var(--color-${s.key})`} strokeWidth={2}
              dot={false} activeDot={{ r: 4 }} animationDuration={800} />
          ))}
          {dots.map((d) => <ReferenceDot key={d.key} x={d.on} y={d.plot} r={4} fill={`var(--color-${marked})`} stroke="var(--background)" />)}
        </ComposedChart>
      </ChartContainer>
    </div>
  );
}

function ComparisonTooltip({ series, currency, active, payload }: { series: ChartSeries[]; currency: string; active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{dayFirst(row.on)}</div>
      {series.map((s) => (row.text[s.key] ? <div key={s.key} className="tabular-nums">{s.label} : {row.text[s.key]}</div> : null))}
      {row.range ? (
        <>
          <div className="tabular-nums">{t('dashboard.projection.ranges_median')} : {formatCurrency(row.range.median, currency)}</div>
          <div className="tabular-nums">{t('dashboard.projection.ranges_band')} : {formatCurrency(row.range.low, currency)} – {formatCurrency(row.range.high, currency)}</div>
        </>
      ) : null}
      {row.marks.map((m) => <div key={m}>{m}</div>)}
    </div>
  );
}
