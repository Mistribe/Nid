import { useTranslation } from 'react-i18next';
import { Bar, BarChart, XAxis, YAxis } from 'recharts';
import { formatMoney } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { toPlot } from '../investments/chart/plot';
import type { DashboardEnvelope, DashboardPerson } from './api';
import { personColor } from './colors';

type Row = { key: string; name: string; value: number; text: string };

const ROW_PX = 32;

/** dataviz.md §2: sorted horizontal bars, one tone; every figure read comes from the server's strings. */
export function EnvelopeChart({ rows }: { rows: DashboardEnvelope[] }) {
  const { t } = useTranslation();
  const data: Row[] = rows.flatMap((r) => (r.converted ? [{ key: r.id, name: r.name, value: toPlot(r.converted.amount), text: formatMoney(r.converted) }] : []));
  if (data.length === 0) return null;
  const config = { value: { label: t('dashboard.split.col_amount'), color: 'var(--chart-1)' } } satisfies ChartConfig;
  return (
    <figure aria-label={t('dashboard.split.chart_envelopes')}>
      {/* The height follows the row count (not an amount), so many envelopes never squeeze their bars. */}
      <ChartContainer config={config} className="aspect-auto w-full" style={{ height: `${data.length * ROW_PX + 16}px` }}>
        <BarChart data={data} layout="vertical" accessibilityLayer margin={{ left: 8, right: 8 }}>
          <XAxis type="number" hide />
          <YAxis type="category" dataKey="name" width={120} tickLine={false} axisLine={false} />
          <ChartTooltip cursor={false} content={<RowTooltip />} />
          <Bar dataKey="value" fill="var(--color-value)" radius={[0, 4, 4, 0]} isAnimationActive={false} />
        </BarChart>
      </ChartContainer>
    </figure>
  );
}

function RowTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium">{row.name}</div>
      <div className="tabular-nums">{row.text}</div>
    </div>
  );
}

/** dataviz.md §2: one stacked bar, one fixed colour per person, normalised by Recharts (`stackOffset="expand"`). */
export function PeopleChart({ people }: { people: DashboardPerson[] }) {
  const { t } = useTranslation();
  if (people.length === 0) return null;
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
            <Bar key={p.id} dataKey={`p${i}`} stackId="people" fill={personColor(p.slot)} stroke="var(--background)" strokeWidth={2} isAnimationActive={false} />
          ))}
        </BarChart>
      </ChartContainer>
    </figure>
  );
}

function PeopleTooltip({ people }: { people: DashboardPerson[] }) {
  return (
    <div className="grid gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      {people.map((p) => <div key={p.id} className="tabular-nums">{`${p.name} · ${formatMoney(p.amount)}`}</div>)}
    </div>
  );
}
