import { useNavigate } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Area, CartesianGrid, ComposedChart, Line, XAxis, YAxis } from 'recharts';
import type { PropertiesSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { formatMoney, Money } from '@/components/Money';
import { Segmented } from '@/components/form/Segmented';
import { SectionCard } from '@/components/page/SectionCard';
import { NEUTRAL_COLOR } from '@/components/ProviderIcon';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '@/lib/plot';
import { monthLabel } from '@/lib/format';
import type { PropertiesOverview, PropertyPoint } from '../api';
import { TableToggle } from '@/components/page/TableToggle';
import '@/locales/properties';

const SPANS = ['12m', '3y', 'all'] as const;
type Span = (typeof SPANS)[number];
type Row = { label: string; balance: number | null; net: number | null; value: number | null; texts: { value: string; balance: string; net: string } };

/**
 * Properties dashboard §3.3: the linked loans' balance in a neutral area under the net value, the value of the properties as a
 * line on top, at each month end then today; the span in the URL, a table on demand. Every figure read is the server's string.
 */
export function NetValueChart({ d, span }: { d: PropertiesOverview; span: Span }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const spanId = useId();
  const [asTable, setAsTable] = useState(false);
  const choose = (next: string) => {
    const history: PropertiesSearch['history'] = next === '12m' ? '12m' : next === 'all' ? 'all' : undefined;
    void navigate({ to: '/properties', replace: true, resetScroll: false, search: (prev: RootSearch & PropertiesSearch) => ({ ...prev, history }) });
  };
  const drawn = d.points.filter((p) => p.netValue).length;
  const label = (p: PropertyPoint, i: number) => {
    if (i === d.points.length - 1) return t('properties_overview.today');
    const [year, month] = p.on.split('-').map(Number);
    return monthLabel(year ?? 0, month ?? 1, 'short');
  };
  const codes = missingCodes(d);

  return (
    <SectionCard title={t('properties_overview.chart_title')} actions={
      <>
        <span id={spanId} className="sr-only">{t('properties_overview.span')}</span>
        <Segmented value={span} onValueChange={choose} aria-labelledby={spanId}
          options={SPANS.map((s) => ({ value: s, label: t(`properties_overview.span_${s}`) }))} />
        {drawn >= 2 ? (
          <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
        ) : null}
      </>
    }>
      {d.points.every((p) => !p.value && !p.linkedBalance) ? <p className="text-muted-foreground">{t('properties_overview.chart_empty')}</p>
        : drawn < 2 || asTable ? <PointsTable d={d} label={label} /> : <Chart d={d} label={label} />}
      <p className="text-sm text-muted-foreground">{t('properties_overview.chart_note')}</p>
      {codes ? <p className="text-sm text-muted-foreground">{t('properties_overview.points_missing', { codes })}</p> : null}
    </SectionCard>
  );
}

/** The currencies a missing rate left out, from the findings' references (`CHF:2026-10-22`). */
export function missingCodes(d: PropertiesOverview): string {
  return [...new Set(d.findings.filter((f) => f.code === 'core.fx_rate_missing').map((f) => (f.reference ?? '').split(':')[0]))].join(', ');
}

function Chart({ d, label }: { d: PropertiesOverview; label: (p: PropertyPoint, i: number) => string }) {
  const { t } = useTranslation();
  // A point whose net value is unknown is not drawn (no estimate yet, or a missing rate).
  const rows: Row[] = d.points.map((p, i) => ({
    label: label(p, i),
    balance: p.netValue && p.linkedBalance ? toPlot(p.linkedBalance.amount) : null,
    net: p.netValue ? toPlot(p.netValue.amount) : null,
    value: p.netValue && p.value ? toPlot(p.value.amount) : null,
    texts: {
      value: p.value ? formatMoney(p.value) : '', balance: p.linkedBalance ? formatMoney(p.linkedBalance) : '', net: p.netValue ? formatMoney(p.netValue) : '',
    },
  }));
  const config = {
    balance: { label: t('properties_overview.series_balance'), color: NEUTRAL_COLOR },
    net: { label: t('properties_overview.series_net'), color: 'var(--chart-1)' },
    value: { label: t('properties_overview.series_value'), color: 'var(--foreground)' },
  } satisfies ChartConfig;
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: d.currency, notation: 'compact' });
  return (
    <ChartContainer config={config} className="h-72 w-full">
      <ComposedChart data={rows} title={t('properties_overview.chart_title')} accessibilityLayer margin={{ top: 8, right: 8 }}>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="label" tickLine={false} axisLine={false} tickMargin={8} minTickGap={24} />
        <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
        <ChartTooltip content={<PointTooltip />} />
        <ChartLegend content={<ChartLegendContent />} />
        <Area dataKey="balance" stackId="worth" type="linear" fill="var(--color-balance)" fillOpacity={0.25} stroke="var(--card)" strokeWidth={2}
          connectNulls={false} dot={false} activeDot={false} animationDuration={800} />
        <Area dataKey="net" stackId="worth" type="linear" fill="var(--color-net)" fillOpacity={0.45} stroke="var(--card)" strokeWidth={2}
          connectNulls={false} dot={false} activeDot={false} animationDuration={800} />
        <Line dataKey="value" stroke="var(--color-value)" strokeWidth={2} type="linear" connectNulls={false} dot={false} activeDot={{ r: 4 }}
          animationDuration={800} />
      </ComposedChart>
    </ChartContainer>
  );
}

function PointTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row || !row.texts.net) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{row.label}</div>
      <div className="tabular-nums">{t('properties_overview.series_value')} : {row.texts.value}</div>
      <div className="tabular-nums">{t('properties_overview.series_balance')} : {row.texts.balance}</div>
      <div className="tabular-nums">{t('properties_overview.series_net')} : {row.texts.net}</div>
    </div>
  );
}

function PointsTable({ d, label }: { d: PropertiesOverview; label: (p: PropertyPoint, i: number) => string }) {
  const { t } = useTranslation();
  const unknown = <span className="text-muted-foreground">{t('properties_overview.unknown')}</span>;
  return (
    <Table aria-label={t('properties_overview.chart_title')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('properties_overview.date')}</TableHead>
          <TableHead scope="col" className="text-right">{t('properties_overview.series_value')}</TableHead>
          <TableHead scope="col" className="text-right">{t('properties_overview.series_balance')}</TableHead>
          <TableHead scope="col" className="text-right">{t('properties_overview.series_net')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {d.points.map((p, i) => !p.value && !p.linkedBalance ? null : (
          <TableRow key={p.on}>
            <TableHead scope="row" className="font-normal">{label(p, i)}</TableHead>
            <TableCell className="text-right">{p.value ? <Money value={p.value} tabular /> : unknown}</TableCell>
            <TableCell className="text-right">{p.linkedBalance ? <Money value={p.linkedBalance} tabular /> : unknown}</TableCell>
            <TableCell className="text-right">{p.netValue ? <Money value={p.netValue} tabular /> : unknown}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
