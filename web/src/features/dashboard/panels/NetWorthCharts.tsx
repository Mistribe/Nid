import { useTranslation } from 'react-i18next';
import { Bar, BarChart, XAxis, YAxis } from 'recharts';
import { formatMoney } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { toPlot } from '@/lib/plot';
import type { AllocationLine, DashboardEnvelope, DashboardPerson } from '@/api/dashboard';
import { classColor, liquidityColor, personColor } from '@/components/colors';
import { useClassName, useLiquidityName } from './allocationNames';

type Row = { key: string; name: string; value: number; text: string };

const ROW_PX = 32;

/** dataviz.md §2: sorted horizontal bars, one tone; every figure read comes from the server's strings. */
function EnvelopeChart({ rows }: { rows: DashboardEnvelope[] }) {
  const { t } = useTranslation();
  const data: Row[] = rows.flatMap((r) => (r.converted ? [{ key: r.id, name: r.name, value: toPlot(r.converted.amount), text: formatMoney(r.converted) }] : []));
  if (data.length === 0) return null;
  const config = { value: { label: t('dashboard.split.col_amount'), color: 'var(--chart-1)' } } satisfies ChartConfig;
  return (
    <figure aria-label={t('dashboard.split.chart_envelopes')}>
      {/* The height follows the row count (not an amount), so many envelopes never squeeze their bars. */}
      <ChartContainer config={config} className="aspect-auto w-full" style={{ height: `${data.length * ROW_PX + 16}px` }}>
        <BarChart data={data} layout="vertical" maxBarSize={24} accessibilityLayer margin={{ left: 8, right: 8 }}>
          <XAxis type="number" hide />
          <YAxis type="category" dataKey="name" width={120} tickLine={false} axisLine={false} />
          <ChartTooltip cursor={false} content={<RowTooltip />} />
          <Bar dataKey="value" fill="var(--color-value)" radius={[0, 4, 4, 0]} animationDuration={800} />
        </BarChart>
      </ChartContainer>
    </figure>
  );
}

function RowTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium">{row.name}</div>
      <div className="tabular-nums">{row.text}</div>
    </div>
  );
}

/** dataviz.md §2: one stacked bar, one fixed colour per person, normalised by Recharts (`stackOffset="expand"`). */
function PeopleChart({ people }: { people: DashboardPerson[] }) {
  const { t } = useTranslation();
  // A person whose loans outweigh their share has a negative amount: a share of the whole cannot draw it; the table says it.
  if (people.length === 0 || people.some((p) => p.amount.amount.startsWith('-'))) return null;
  const datum: Record<string, string | number> = { key: 'people', ...Object.fromEntries(people.map((p, i) => [`p${i}`, toPlot(p.amount.amount)])) };
  const config = Object.fromEntries(people.map((p, i) => [`p${i}`, { label: p.name, color: personColor(p.slot) }])) satisfies ChartConfig;
  return (
    <figure aria-label={t('dashboard.split.chart_people')}>
      <ChartContainer config={config} className="aspect-auto h-12 w-full">
        <BarChart data={[datum]} layout="vertical" stackOffset="expand" accessibilityLayer margin={{ left: 0, right: 0 }}>
          <XAxis type="number" hide />
          <YAxis type="category" dataKey="key" hide />
          <ChartTooltip cursor={false} content={({ active }) => (active ? <PeopleTooltip people={people} /> : null)} />
          {people.map((p, i) => (
            <Bar key={p.id} dataKey={`p${i}`} stackId="people" fill={personColor(p.slot)} stroke="var(--background)" strokeWidth={2} animationDuration={800} />
          ))}
        </BarChart>
      </ChartContainer>
    </figure>
  );
}

function PeopleTooltip({ people }: { people: DashboardPerson[] }) {
  return (
    <div className="grid gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      {people.map((p) => <div key={p.id} className="tabular-nums">{`${p.name} · ${formatMoney(p.amount)}`}</div>)}
    </div>
  );
}

/** Issue #104: one stacked bar of the assets, a fixed colour per class or liquidity; the table below gives every figure. */
function AllocationChart({ lines, color, name }: { lines: AllocationLine[]; color: (key: string) => string; name: (key: string) => string }) {
  const { t } = useTranslation();
  const shown = lines.filter((l) => l.amount.amount !== '0.00');
  // A negative part (an overdraft) cannot be drawn as a share of the whole; the table says it.
  if (shown.length === 0 || shown.some((l) => l.amount.amount.startsWith('-'))) return null;
  const datum: Record<string, string | number> = { key: 'allocation', ...Object.fromEntries(shown.map((l) => [l.key, toPlot(l.amount.amount)])) };
  const config = Object.fromEntries(shown.map((l) => [l.key, { label: name(l.key), color: color(l.key) }])) satisfies ChartConfig;
  return (
    <figure aria-label={t('dashboard.allocation.chart')}>
      <ChartContainer config={config} className="aspect-auto h-12 w-full">
        <BarChart data={[datum]} layout="vertical" stackOffset="expand" accessibilityLayer margin={{ left: 0, right: 0 }}>
          <XAxis type="number" hide />
          <YAxis type="category" dataKey="key" hide />
          <ChartTooltip cursor={false} content={({ active }) => (active ? (
            <div className="grid gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
              {shown.map((l) => <div key={l.key} className="tabular-nums">{`${name(l.key)} · ${formatMoney(l.amount)}`}</div>)}
            </div>
          ) : null)} />
          {shown.map((l) => (
            <Bar key={l.key} dataKey={l.key} stackId="allocation" fill={color(l.key)} stroke="var(--background)" strokeWidth={2} animationDuration={800} />
          ))}
        </BarChart>
      </ChartContainer>
    </figure>
  );
}

export type CompositionView = 'type' | 'person' | 'class' | 'liquidity';

/** The views of the net worth's composition; Recharts stays out of the home's initial chunk. */
export function NetWorthCharts({ view, rows, people, allocation }: {
  view: CompositionView; rows: DashboardEnvelope[]; people: DashboardPerson[]; allocation?: { classes: AllocationLine[]; liquidity: AllocationLine[] } | null;
}) {
  const className = useClassName();
  const liquidityName = useLiquidityName();
  if (view === 'class') return allocation ? <AllocationChart lines={allocation.classes} color={classColor} name={className} /> : null;
  if (view === 'liquidity') return allocation ? <AllocationChart lines={allocation.liquidity} color={liquidityColor} name={liquidityName} /> : null;
  return view === 'type' ? <EnvelopeChart rows={rows} /> : <PeopleChart people={people} />;
}
