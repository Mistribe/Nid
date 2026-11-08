import { useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, Cell, ReferenceLine, XAxis, YAxis } from 'recharts';
import type { IncomeDetailSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { Segmented } from '@/components/form/Segmented';
import { formatMoney, Money } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { percent, monthLabel } from '@/lib/format';
import { toPlot } from '@/lib/plot';
import type { IncomeChange, IncomeSourceOverview } from '@/api/income';
import { TableToggle } from '@/components/page/TableToggle';
import '@/locales/income';

type Months = IncomeSourceOverview['months'];
type Month = Months[number];
type Row = { key: string; label: string; month: Month } & Record<string, unknown>;

// chart-4 stays the one-offs' (as on the list page), chart-6 and chart-8 read as up and down.
// ponytail: past five components the colours repeat; a source has two or three in practice.
const PALETTE = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-5)', 'var(--chart-7)'];
const PERIODS = ['12m', '3y', 'all'] as const;
const pad = (n: number) => String(n).padStart(2, '0');

/**
 * Income dashboard §4, « Évolution »: the source's cash month by month, one stacked colour per component and one for the
 * one-offs, upcoming months dimmed, a vertical mark at each amount change with its percent; a table on demand.
 */
export function SourceChart({ months, changes }: { months: Months; changes: IncomeChange[] }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const { id } = useParams({ from: '/income/$id' });
  const search: RootSearch & IncomeDetailSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const period = search.period ?? '12m';
  const choose = (next: string) => {
    const chosen: IncomeDetailSearch['period'] = next === '3y' || next === 'all' ? next : undefined;
    void navigate({
      from: '/income/$id', to: '/income/$id', params: { id }, replace: true, resetScroll: false,
      search: (prev: RootSearch & IncomeDetailSearch) => ({ ...prev, period: chosen }),
    });
  };

  const labels = [...new Set(months.flatMap((m) => m.parts.map((p) => p.label)))].sort((a, b) => a.localeCompare(b, 'fr'));
  const config: ChartConfig = {
    ...Object.fromEntries(labels.map((l, i) => [`l${i}`, { label: l, color: PALETTE[i % PALETTE.length] }])),
    oneOff: { label: t('income_detail.evolution.one_off'), color: 'var(--chart-4)' },
  };
  const rows: Row[] = months.map((m) => ({
    key: `${m.year}-${pad(m.month)}`, label: monthLabel(m.year, m.month, 'short'), month: m,
    ...Object.fromEntries(m.parts.map((p) => [`l${labels.indexOf(p.label)}`, toPlot(p.amount.amount)])),
    oneOff: toPlot(m.oneOff.amount),
  }));
  // A change outside the drawn months has no mark.
  const marks = changes.filter((c) => c.kind === 'change').flatMap((c) => {
    const row = rows.find((r) => r.key === c.on.slice(0, 7));
    return row ? [{ key: `${c.phaseId}-${c.on}`, x: row.label, text: c.percent != null ? t('income_detail.evolution.pct', { value: percent(c.percent, true) }) : '' }] : [];
  });
  const currency = months[0]?.total.currency ?? 'EUR';
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  const dim = (r: Row) => (r.month.upcoming ? 0.4 : 1);
  const series = [...labels.map((_, i) => `l${i}`), 'oneOff'];
  // #383: a window without a payment says so rather than drawing axes over nothing.
  const empty = months.every((m) => toPlot(m.total.amount) === 0);

  return (
    <SectionCard title={t('income_detail.evolution.title')} actions={
      <>
        <Segmented aria-label={t('income_detail.evolution.period')} value={period} onValueChange={choose}
          options={PERIODS.map((p) => ({ value: p, label: t(`income_detail.evolution.${p}`) }))} />
        <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
      </>
    }>
        {asTable ? <SourceMonthsTable months={months} labels={labels} /> : empty ? (
          <p className="text-sm text-muted-foreground">{t(`income_detail.evolution.empty_${period}`)}</p>
        ) : (
          <ChartContainer config={config} className="h-64 w-full">
            <BarChart data={rows} maxBarSize={24} title={t('income_detail.evolution.chart')} accessibilityLayer>
              <CartesianGrid vertical={false} />
              <XAxis dataKey="label" tickLine={false} tickMargin={8} minTickGap={24} />
              <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
              <ChartTooltip cursor={false} content={<MonthTooltip />} />
              <ChartLegend content={<ChartLegendContent />} />
              {series.map((key, i) => (
                <Bar key={key} dataKey={key} stackId="month" fill={`var(--color-${key})`} stroke="var(--card)" strokeWidth={2} animationDuration={800}
                  radius={i === series.length - 1 ? [4, 4, 0, 0] : undefined}>
                  {rows.map((r) => <Cell key={r.key} fillOpacity={dim(r)} />)}
                </Bar>
              ))}
              {marks.map((m) => (
                <ReferenceLine key={m.key} x={m.x} stroke="var(--muted-foreground)" strokeDasharray="3 3"
                  label={{ value: m.text, position: 'top', fill: 'var(--foreground)', fontSize: 12 }} />
              ))}
            </BarChart>
          </ChartContainer>
        )}
    </SectionCard>
  );
}

function MonthTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  const { month: m } = row;
  return (
    <div className="grid min-w-44 gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium">{monthLabel(m.year, m.month)}{m.upcoming ? ` · ${t('income_detail.evolution.upcoming')}` : ''}</div>
      {m.parts.map((p) => (
        <div key={p.label} className="flex justify-between gap-4"><span>{p.label}</span><span className="tabular-nums">{formatMoney(p.amount)}</span></div>
      ))}
      <div className="flex justify-between gap-4"><span>{t('income_detail.evolution.one_off')}</span><span className="tabular-nums">{formatMoney(m.oneOff)}</span></div>
      <div className="flex justify-between gap-4 border-t pt-1 font-medium"><span>{t('income_detail.evolution.total')}</span><span className="tabular-nums">{formatMoney(m.total)}</span></div>
    </div>
  );
}

/** The chart's figures as the server gives them, one column per component. */
function SourceMonthsTable({ months, labels }: { months: Months; labels: string[] }) {
  const { t } = useTranslation();
  return (
    <Table aria-label={t('income_detail.evolution.table')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('income_detail.evolution.col_month')}</TableHead>
          {labels.map((l) => <TableHead key={l} className="text-right">{l}</TableHead>)}
          <TableHead className="text-right">{t('income_detail.evolution.one_off')}</TableHead>
          <TableHead className="text-right">{t('income_detail.evolution.total')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {months.map((m) => (
          <TableRow key={`${m.year}-${m.month}`}>
            <TableHead scope="row" className="font-normal">
              {monthLabel(m.year, m.month)}{m.upcoming ? ` · ${t('income_detail.evolution.upcoming')}` : ''}
            </TableHead>
            {labels.map((l) => {
              const part = m.parts.find((p) => p.label === l);
              return <TableCell key={l} className="text-right">{part ? <Money value={part.amount} tabular /> : '—'}</TableCell>;
            })}
            <TableCell className="text-right"><Money value={m.oneOff} tabular /></TableCell>
            <TableCell className="text-right font-medium"><Money value={m.total} tabular /></TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
