import { useTranslation } from 'react-i18next';
import { CartesianGrid, Line, LineChart, ReferenceDot, XAxis, YAxis } from 'recharts';
import { formatCurrency } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { cn } from '@/lib/utils';
import type { HouseholdPoint, ProjectionMilestone } from '@/features/dashboard/api';
import { toPlot } from '../../investments/chart/plot';
import type { ProjectionComparison } from './api';
import '@/locales/scenarios';

type Row = { on: string; base?: number; scenario?: number; baseText?: string; scenarioText?: string; marks: string[] };

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
 * Spec §8 and dataviz.md: the base (`--chart-1`) and the scenario (`--chart-2`) as two lines on the same grid, a dot on each of the
 * scenario's milestones, the x axis ticking once a year. `toPlot` only places the marks: the tooltip shows the server's strings.
 */
export function ProjectionComparisonChart({ comparison, real, className }: { comparison: ProjectionComparison; real: boolean; className?: string }) {
  const { t } = useTranslation();
  const text = useMilestoneText();
  const currency = comparison.base.currency;
  const config = {
    base: { label: t('scenarios.projection.result.base'), color: 'var(--chart-1)' },
    scenario: { label: t('scenarios.projection.result.scenario'), color: 'var(--chart-2)' },
  } satisfies ChartConfig;
  const rows = new Map<string, Row>();
  const row = (on: string) => rows.get(on) ?? rows.set(on, { on, marks: [] }).get(on)!;
  for (const p of comparison.base.points) Object.assign(row(p.on), { base: toPlot(reading(p, real)), baseText: formatCurrency(reading(p, real), currency) });
  for (const p of comparison.scenario.points) Object.assign(row(p.on), { scenario: toPlot(reading(p, real)), scenarioText: formatCurrency(reading(p, real), currency) });
  const data = [...rows.values()].sort((a, b) => a.on.localeCompare(b.on));
  // A milestone sits on the first scenario point at or after its date (ISO dates compare as strings).
  const dots = comparison.scenario.milestones.flatMap((m) => {
    const at = data.find((r) => r.on >= m.on && r.scenario !== undefined);
    if (!at) return [];
    at.marks.push(text(m));
    return [{ key: `${m.kind}-${m.on}-${m.item ?? m.label}`, on: at.on, plot: at.scenario! }];
  });
  const ticks = comparison.base.points.filter(isYearEnd).map((p) => p.on);
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  return (
    <div className="space-y-2">
      <ul aria-label={t('scenarios.projection.result.legend')} className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
        {(['base', 'scenario'] as const).map((key) => (
          <li key={key} className="flex items-center gap-1.5">
            <span aria-hidden="true" className="h-0.5 w-4 rounded" style={{ background: config[key].color }} />
            {config[key].label}
          </li>
        ))}
      </ul>
      <ChartContainer config={config} className={cn('h-56 w-full', className)}>
        <LineChart data={data} title={t('scenarios.projection.result.chart_title')} accessibilityLayer>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="on" ticks={ticks} tickFormatter={(on: string) => on.slice(0, 4)} tickLine={false} axisLine={false} tickMargin={8} />
          <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
          <ChartTooltip content={<ComparisonTooltip />} />
          <Line dataKey="base" type="monotone" stroke="var(--color-base)" strokeWidth={2} dot={false} activeDot={{ r: 4 }} animationDuration={800} />
          <Line dataKey="scenario" type="monotone" stroke="var(--color-scenario)" strokeWidth={2} dot={false} activeDot={{ r: 4 }} animationDuration={800} />
          {dots.map((d) => <ReferenceDot key={d.key} x={d.on} y={d.plot} r={4} fill="var(--color-scenario)" stroke="var(--background)" />)}
        </LineChart>
      </ChartContainer>
    </div>
  );
}

function ComparisonTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{dayFirst(row.on)}</div>
      {row.baseText ? <div className="tabular-nums">{t('scenarios.projection.result.base')} : {row.baseText}</div> : null}
      {row.scenarioText ? <div className="tabular-nums">{t('scenarios.projection.result.scenario')} : {row.scenarioText}</div> : null}
      {row.marks.map((m) => <div key={m}>{m}</div>)}
    </div>
  );
}
