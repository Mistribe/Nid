import { useNavigate } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Area, CartesianGrid, ComposedChart, Line, XAxis, YAxis } from 'recharts';
import type { InvestmentsSearch, RootSearch } from '@/app/router';
import { formatMoney, Money } from '@/components/Money';
import { Segmented } from '@/components/form/Segmented';
import { SectionCard } from '@/components/page/SectionCard';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { monthLabel } from '../../income/expected/months';
import { toPlot } from '../chart/plot';
import type { InvestmentsOverview, OverviewPoint } from '../api';
import { kindText } from '../format';
import { KIND_ORDER, kindColor } from './colors';
import { TableToggle } from '@/components/page/TableToggle';
import '@/locales/investments';

const SPANS = ['12m', '3y', 'all'] as const;
type Span = (typeof SPANS)[number];
type Row = { on: string; label: string; total: number | null; net: number | null; totalText: string; netText: string; paidIn: boolean; texts: Record<string, string> }
  & Record<string, number | null | string | boolean | Record<string, string>>;

const key = (kind: string) => `k_${kind}`;

/**
 * Investments dashboard §3.3: the value by kind of envelope (stacked areas, 2 px apart) or in total, and the net contributions
 * dashed, at each month end then today; the span in the URL, a table on demand. Every figure read is the server's string.
 */
export function ValueChart({ d, span }: { d: InvestmentsOverview; span: Span }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const spanId = useId();
  const viewId = useId();
  const [view, setView] = useState<'kind' | 'total'>('kind');
  const [asTable, setAsTable] = useState(false);
  const choose = (next: string) => {
    const curve: InvestmentsSearch['curve'] = next === '3y' ? '3y' : next === 'all' ? 'all' : undefined;
    void navigate({ to: '/investments', replace: true, resetScroll: false, search: (prev: RootSearch & InvestmentsSearch) => ({ ...prev, curve }) });
  };
  const known = d.points.filter((p) => p.value).length;
  const label = (p: OverviewPoint, i: number) => {
    if (i === d.points.length - 1) return t('investments_overview.today');
    const [year, month] = p.on.split('-').map(Number);
    return monthLabel(year ?? 0, month ?? 1, 'short');
  };
  const codes = missingCodes(d);

  return (
    <SectionCard title={t('investments_overview.chart_title')} actions={
      <>
        <span id={viewId} className="sr-only">{t('investments_overview.view')}</span>
        <Segmented value={view} onValueChange={(v) => setView(v === 'total' ? 'total' : 'kind')} aria-labelledby={viewId}
          options={[{ value: 'kind', label: t('investments_overview.view_kind') }, { value: 'total', label: t('investments_overview.view_total') }]} />
        <span id={spanId} className="sr-only">{t('investments_overview.span')}</span>
        <Segmented value={span} onValueChange={choose} aria-labelledby={spanId}
          options={SPANS.map((s) => ({ value: s, label: t(`investments_overview.span_${s}`) }))} />
        {known >= 2 ? (
          <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
        ) : null}
      </>
    }>
      {known === 0 ? <p className="text-muted-foreground">{t('investments_overview.chart_empty')}</p>
        : known < 2 || asTable ? <PointsTable d={d} label={label} /> : <Chart d={d} label={label} view={view} />}
      {codes && d.points.some((p) => !p.value && p.byKind.length > 0)
        ? <p className="text-sm text-muted-foreground">{t('investments_overview.points_missing', { codes })}</p> : null}
    </SectionCard>
  );
}

/** The currencies a missing rate left out, from the findings' references (`USD:2026-10-22`). */
export function missingCodes(d: InvestmentsOverview): string {
  return [...new Set(d.findings.filter((f) => f.code === 'core.fx_rate_missing').map((f) => (f.reference ?? '').split(':')[0]))].join(', ');
}

/** The kinds the curve draws, in their fixed order (D10). */
function kindsOf(d: InvestmentsOverview): string[] {
  const present = new Set(d.points.flatMap((p) => p.byKind.map((k) => k.kind)));
  return [...KIND_ORDER.filter((k) => present.has(k)), ...[...present].filter((k) => !(KIND_ORDER as readonly string[]).includes(k)).sort()];
}

function Chart({ d, label, view }: { d: InvestmentsOverview; label: (p: OverviewPoint, i: number) => string; view: 'kind' | 'total' }) {
  const { t } = useTranslation();
  const kinds = kindsOf(d);
  const rows: Row[] = d.points.map((p, i) => {
    const row: Row = {
      on: p.on, label: label(p, i), total: p.value ? toPlot(p.value.amount) : null, net: p.netContributions ? toPlot(p.netContributions.amount) : null,
      totalText: p.value ? formatMoney(p.value) : '', netText: p.netContributions ? formatMoney(p.netContributions) : '', paidIn: p.paidIn, texts: {},
    };
    for (const kind of kinds) row[key(kind)] = null;
    // A point whose total is unknown is not drawn (a missing rate): the note under the chart says so.
    if (p.value)
      for (const k of p.byKind) {
        row[key(k.kind)] = k.value ? toPlot(k.value.amount) : null;
        if (k.value) row.texts[k.kind] = formatMoney(k.value);
      }
    return row;
  });
  const config = {
    ...Object.fromEntries(kinds.map((k) => [key(k), { label: kindText(t, k), color: kindColor(k) }])),
    total: { label: t('investments_overview.series_value'), color: 'var(--chart-1)' },
    net: { label: t('investments_overview.series_net'), color: 'var(--foreground)' },
  } satisfies ChartConfig;
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: d.currency, notation: 'compact' });
  return (
    <ChartContainer config={config} className="h-72 w-full">
      <ComposedChart data={rows} title={t('investments_overview.chart_title')} accessibilityLayer margin={{ top: 8, right: 8 }}>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="label" tickLine={false} axisLine={false} tickMargin={8} minTickGap={24} />
        <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
        <ChartTooltip content={<PointTooltip kinds={kinds} />} />
        <ChartLegend content={<ChartLegendContent />} />
        {view === 'kind'
          ? kinds.map((k) => (
            <Area key={k} dataKey={key(k)} stackId="value" type="linear" fill={kindColor(k)} fillOpacity={0.6}
              stroke="var(--card)" strokeWidth={2} connectNulls={false} dot={false} activeDot={false} animationDuration={800} />
          ))
          : <Area dataKey="total" type="linear" fill="var(--color-total)" fillOpacity={0.15} stroke="var(--color-total)" strokeWidth={2}
              connectNulls={false} dot={false} animationDuration={800} />}
        <Line dataKey="net" stroke="var(--color-net)" strokeWidth={2} strokeDasharray="5 4" type="stepAfter" connectNulls={false}
          dot={false} activeDot={{ r: 4 }} animationDuration={800} />
      </ComposedChart>
    </ChartContainer>
  );
}

function PointTooltip({ active, payload, kinds }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }>; kinds: string[] }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row || !row.totalText) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{row.label}</div>
      <div className="tabular-nums">{t('investments_overview.series_value')} : {row.totalText}</div>
      {row.paidIn ? <PaidIn /> : null}
      {kinds.filter((k) => row.texts[k]).map((k) => (
        <div key={k} className="flex items-center gap-1.5 tabular-nums">
          <span aria-hidden="true" className="size-2 rounded-sm" style={{ background: kindColor(k) }} />{kindText(t, k)} : {row.texts[k]}
        </div>
      ))}
      <div className="tabular-nums">{t('investments_overview.series_net')} : {row.netText}</div>
    </div>
  );
}

/** Issue #325: some envelope counts at what was paid into it, not at a valuation: words, not colour, as an estimate. */
function PaidIn() {
  const { t } = useTranslation();
  return <span className="block text-xs text-muted-foreground">{t('investments_overview.paid_in')}</span>;
}

function PointsTable({ d, label }: { d: InvestmentsOverview; label: (p: OverviewPoint, i: number) => string }) {
  const { t } = useTranslation();
  const kinds = kindsOf(d);
  const unknown = <span className="text-muted-foreground">{t('investments_overview.unknown')}</span>;
  return (
    <Table aria-label={t('investments_overview.chart_title')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('investments_overview.date')}</TableHead>
          {kinds.map((k) => <TableHead key={k} scope="col" className="text-right">{kindText(t, k)}</TableHead>)}
          <TableHead scope="col" className="text-right">{t('investments_overview.series_value')}</TableHead>
          <TableHead scope="col" className="text-right">{t('investments_overview.series_net')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {d.points.map((p, i) => p.byKind.length === 0 ? null : (
          <TableRow key={p.on}>
            <TableHead scope="row" className="font-normal">{label(p, i)}</TableHead>
            {kinds.map((k) => {
              const value = p.byKind.find((x) => x.kind === k)?.value;
              return <TableCell key={k} className="text-right">{value ? <Money value={value} tabular /> : p.value ? '' : unknown}</TableCell>;
            })}
            <TableCell className="text-right">{p.value ? <Money value={p.value} tabular /> : unknown}{p.paidIn ? <PaidIn /> : null}</TableCell>
            <TableCell className="text-right">{p.netContributions ? <Money value={p.netContributions} tabular /> : unknown}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
