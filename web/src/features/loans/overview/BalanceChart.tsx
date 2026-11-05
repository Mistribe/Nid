import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Area, AreaChart, CartesianGrid, DefaultZIndexes, ReferenceLine, usePlotArea, useYAxisDomain, XAxis, YAxis, ZIndexLayer } from 'recharts';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { monthLabel } from '../../income/expected/months';
import { toPlot } from '../../investments/chart/plot';
import type { LoansOverview } from '../api';
import { loanColor } from './colors';
import { SectionCard } from '@/components/page/SectionCard';
import { TableToggle } from '@/components/page/TableToggle';
import '@/locales/loans';

type Row = { i: number; month: string; total: string; values: (string | null)[] } & Record<string, number | null | string | (string | null)[]>;

const text = (value?: MoneyValue | null) => (value ? formatMoney(value) : '—');
const label = (month: string, style: 'long' | 'short' = 'long') => {
  const [year, m] = month.split('-').map(Number);
  return monthLabel(year ?? 0, m ?? 1, style);
};
const key = (rank: number) => `l${rank}`;

/** The name of a loan of the overview by its id, and its rank (its colour). */
export function useLoanNames(d: LoansOverview) {
  const { t } = useTranslation();
  const rank = new Map(d.loans.map((l, i) => [l.id, i]));
  return {
    rankOf: (id: string) => rank.get(id) ?? -1,
    nameOf: (id: string) => d.loans[rank.get(id) ?? -1]?.name ?? t('loans_overview.unknown_loan'),
  };
}

/**
 * Loans dashboard §3.3 and dataviz.md §6: each loan's balance stacked month by month to the last payment, 2 px between the
 * fills and a direct label in each area tall enough, the events as dashed lines listed under the chart; a table on demand.
 * Every figure read is the server's string; `toPlot` only sizes the marks.
 */
export function BalanceChart({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const { nameOf } = useLoanNames(d);
  const missing = d.months.some((m) => !m.total);
  const events = d.events.map((e) => ({
    ...e,
    what: e.code === 'realestate.phase_ends'
      ? t('loans_overview.event_short_phase_ends', { n: e.phaseSeq, name: nameOf(e.loanId) })
      : t('loans_overview.event_short_loan_ends', { name: nameOf(e.loanId) }),
  }));
  // Its own card, so « Afficher en tableau » sits in the card's header as on every other chart (dataviz.md §6).
  return (
    <SectionCard title={t('loans_overview.chart_title')} className="space-y-3" actions={<TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />}>
      {asTable ? <MonthsTable d={d} /> : <Chart d={d} />}
      {events.length > 0 ? (
        <ul className="space-y-1 text-sm text-muted-foreground">
          {events.map((e) => (
            <li key={`${e.date}/${e.loanId}/${e.code}`}>
              {t('loans_overview.event_line', { date: label(e.date.slice(0, 7)), what: e.what })}
            </li>
          ))}
        </ul>
      ) : null}
      {missing ? <p className="text-sm text-muted-foreground">{t('loans_overview.months_missing')}</p> : null}
      {d.convertedIsEstimate ? <p className="text-sm text-muted-foreground">{t('loans_overview.estimate')}</p> : null}
    </SectionCard>
  );
}

function rowsOf(d: LoansOverview): Row[] {
  const rank = new Map(d.loans.map((l, i) => [l.id, i]));
  return d.months.map((m, i) => {
    const row: Row = { i, month: m.month, total: text(m.total), values: d.loans.map(() => null) };
    d.loans.forEach((_, k) => { row[key(k)] = null; });
    for (const b of m.byLoan) {
      const k = rank.get(b.loanId);
      if (k === undefined) continue;
      row.values[k] = text(b.balance);
      // A month whose total is unknown is not drawn (spec §3.3): it says so under the chart.
      row[key(k)] = m.total && b.balance ? toPlot(b.balance.amount) : null;
    }
    return row;
  });
}

function Chart({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const rows = rowsOf(d);
  const config = Object.fromEntries(d.loans.map((l, k) => [key(k), { label: l.name, color: loanColor(k) }])) satisfies ChartConfig;
  // The axis shows the years: a tick on each January, the first month when there is none.
  const ticks = rows.filter((r) => r.month.endsWith('-01')).map((r) => r.i);
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: d.currency, notation: 'compact' });
  const index = new Map(d.months.map((m, i) => [m.month, i]));
  return (
    <figure aria-label={t('loans_overview.chart_title')}>
      <ChartContainer config={config} className="h-72 w-full">
        <AreaChart data={rows} accessibilityLayer margin={{ top: 8, right: 8 }}>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="i" type="number" domain={[0, Math.max(0, rows.length - 1)]} ticks={ticks.length > 0 ? ticks : [0]}
            tickFormatter={(i: number) => rows[i]?.month.slice(0, 4) ?? ''} tickLine={false} axisLine={false} tickMargin={8} />
          <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
          <ChartTooltip content={<MonthTooltip names={d.loans.map((l) => l.name)} />} />
          {d.events.map((e) => {
            const at = index.get(e.date.slice(0, 7));
            return at === undefined ? null
              : <ReferenceLine key={`${e.date}/${e.loanId}/${e.code}`} x={at} stroke="var(--muted-foreground)" strokeDasharray="4 4" />;
          })}
          {d.loans.map((l, k) => (
            <Area key={l.id} dataKey={key(k)} name={l.name} stackId="balance" type="linear" fill={loanColor(k)} fillOpacity={0.6}
              stroke="var(--card)" strokeWidth={2} connectNulls={false} dot={false} activeDot={false} animationDuration={800} />
          ))}
          <DirectLabels rows={rows} names={d.loans.map((l) => l.name)} />
        </AreaChart>
      </ChartContainer>
    </figure>
  );
}

const LABEL_HEIGHT = 16;

/**
 * Each loan's name inside its own area, where that area is thickest away from the axis, when it is tall enough to hold
 * it, on the labels' layer above the areas: a positioning in the chart's coordinates, from plotting numbers only.
 */
function DirectLabels({ rows, names }: { rows: Row[]; names: string[] }) {
  const plot = usePlotArea();
  const domain = useYAxisDomain();
  const top = Array.isArray(domain) ? Number(domain[domain.length - 1]) : NaN;
  if (!plot || !(top > 0) || rows.length === 0) return null;
  const x = (i: number) => plot.x + (rows.length > 1 ? (i / (rows.length - 1)) * plot.width : plot.width / 2);
  const y = (v: number) => plot.y + plot.height - (v / top) * plot.height;
  return (
    <ZIndexLayer zIndex={DefaultZIndexes.label}>
    <g aria-hidden="true">
      {names.map((name, k) => {
        let best: { i: number; low: number; high: number } | null = null;
        // A decreasing balance is thickest at its first month, on the axis: the search starts a tenth into the curve.
        for (const r of rows.slice(Math.floor(rows.length / 10))) {
          const own = r[key(k)];
          if (typeof own !== 'number') continue;
          let low = 0;
          for (let j = 0; j < k; j++) {
            const below = r[key(j)];
            if (typeof below === 'number') low += below;
          }
          if (!best || own > best.high - best.low) best = { i: r.i, low, high: low + own };
        }
        if (!best || y(best.low) - y(best.high) < LABEL_HEIGHT) return null;
        // Kept inside the plot: a label at an edge is anchored to that edge.
        const at = x(best.i);
        const anchor = at < plot.x + plot.width * 0.15 ? 'start' : at > plot.x + plot.width * 0.85 ? 'end' : 'middle';
        return (
          <text key={name + k} x={anchor === 'start' ? at + 6 : anchor === 'end' ? at - 6 : at} y={(y(best.low) + y(best.high)) / 2}
            textAnchor={anchor} dominantBaseline="middle" className="fill-foreground text-xs font-medium">
            {name}
          </text>
        );
      })}
    </g>
    </ZIndexLayer>
  );
}

function MonthTooltip({ active, payload, names }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }>; names: string[] }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium">{label(row.month)}</div>
      <div className="tabular-nums">{t('loans_overview.total')} : {row.total}</div>
      {row.values.map((v, k) => (v === null ? null : (
        <div key={names[k] + String(k)} className="flex items-center gap-1.5 tabular-nums">
          <span aria-hidden="true" className="size-2 rounded-full" style={{ background: loanColor(k) }} />
          {names[k]} : {v}
        </div>
      )))}
    </div>
  );
}

function MonthsTable({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const rows = rowsOf(d);
  return (
    <Table aria-label={t('loans_overview.chart_table')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('loans_overview.month')}</TableHead>
          {d.loans.map((l) => <TableHead key={l.id} scope="col" className="text-right">{l.name}</TableHead>)}
          <TableHead scope="col" className="text-right">{t('loans_overview.total')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((r) => (
          <TableRow key={r.month}>
            <TableHead scope="row" className="font-normal">{label(r.month, 'short')}</TableHead>
            {r.values.map((v, k) => <TableCell key={d.loans[k]!.id} className="text-right tabular-nums">{v ?? '—'}</TableCell>)}
            <TableCell className="text-right tabular-nums">{r.total}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

export { label as monthText };
