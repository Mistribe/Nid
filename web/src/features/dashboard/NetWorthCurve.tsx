import { useNavigate, useSearch } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Area, CartesianGrid, ComposedChart, Line, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { DashboardSearch, RootSearch } from '@/app/router';
import { formatMoney, Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Segmented } from '@/components/form/Segmented';
import { SectionCard } from '@/components/page/SectionCard';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Skeleton } from '@/components/ui/skeleton';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { monthText } from '@/components/LocalDate';
import { monthLabel } from '../income/expected/months';
import { toPlot } from '../investments/chart/plot';
import { NET_WORTH, type NetWorthHistory, type NetWorthPointView } from './api';
import { TableToggle } from '@/components/page/TableToggle';

const PERIODS = ['12m', '3y', 'all'] as const;
type Period = (typeof PERIODS)[number];
const reading = (p: NetWorthPointView, real: boolean) => (real ? { value: p.realValue, paid: p.realContributions } : { value: p.value, paid: p.contributions });
type Row = { on: string; label: string; value: number | null; paid: number | null; valueText: string; paidText: string };

/** Spec §4.1: the household's value and cumulative contributions at each month end, then today; period and table toggle in the card header. */
export function NetWorthCurve() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const periodId = useId();
  const [asTable, setAsTable] = useState(false);
  const search: RootSearch & DashboardSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const period: Period = search.curve === '3y' || search.curve === 'all' ? search.curve : '12m';
  const readingId = useId();
  const answer = $api.useQuery('get', NET_WORTH, { params: { query: { scope, period } } });
  const choose = (next: string) => {
    const curve: DashboardSearch['curve'] = next === '3y' ? '3y' : next === 'all' ? 'all' : undefined;
    void navigate({ to: '/dashboard', replace: true, resetScroll: false, search: (prev: RootSearch & DashboardSearch) => ({ ...prev, curve }) });
  };
  const chooseReading = (next: string) => {
    void navigate({ to: '/dashboard', replace: true, resetScroll: false, search: (prev: RootSearch & DashboardSearch) => ({ ...prev, curveReal: next === 'real' ? (1 as const) : undefined }) });
  };
  const data = answer.data;
  const real = search.curveReal === 1 && !!data?.realTo;
  const known = data ? data.points.filter((p) => p.value).length : 0;

  return (
    <SectionCard title={t('dashboard.curve.title')} actions={
      <>
        <span id={periodId} className="sr-only">{t('dashboard.curve.period')}</span>
        <Segmented value={period} onValueChange={choose} aria-labelledby={periodId}
          options={PERIODS.map((p) => ({ value: p, label: t(`dashboard.curve.period_${p}`) }))} />
        {data?.realTo ? (
          <>
            <span id={readingId} className="sr-only">{t('dashboard.curve.reading')}</span>
            <Segmented value={real ? 'real' : 'nominal'} onValueChange={chooseReading} aria-labelledby={readingId}
              options={[{ value: 'nominal', label: t('dashboard.curve.nominal') }, { value: 'real', label: t('dashboard.curve.real') }]} />
          </>
        ) : null}
        {known >= 2 ? (
          <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
        ) : null}
      </>
    }>
      <Body data={data} error={answer.error} asTable={asTable} real={real} />
      <RealNote data={data} real={real} wanted={search.curveReal === 1} />
    </SectionCard>
  );
}

function RealNote({ data, real, wanted }: { data?: NetWorthHistory; real: boolean; wanted: boolean }) {
  const { t } = useTranslation();
  if (!data) return null;
  const text = wanted && data.realMissing ? t(`dashboard.curve.real_missing.${data.realMissing === 'currency' || data.realMissing === 'not_published' ? data.realMissing : 'unavailable'}`)
    : real && data.realTo ? t('dashboard.curve.real_to', { month: monthText(data.realTo) })
    : null;
  const gaps = real && data.points.some((p) => p.value && !p.realValue);
  return text || gaps ? (
    <p className="mt-2 text-xs text-muted-foreground">
      {text ? <span>{text}</span> : null}{text && gaps ? ' · ' : null}{gaps ? <span>{t('dashboard.curve.real_gaps')}</span> : null}
    </p>
  ) : null;
}

function Body({ data, error, asTable, real }: { data?: NetWorthHistory; error: unknown; asTable: boolean; real: boolean }) {
  const { t } = useTranslation();
  if (!data && error) return <ProblemMessage problem={present(toProblem(error))} />;
  if (!data) return <Skeleton role="status" aria-busy="true" aria-label={t('dashboard.curve.loading')} className="h-64 w-full rounded-xl" />;
  const known = data.points.filter((p) => p.value);
  if (known.length === 0) return <p className="text-muted-foreground">{t('dashboard.curve.empty')}</p>;
  const label = (p: NetWorthPointView, i: number) => {
    if (i === data.points.length - 1) return t('dashboard.curve.today');
    const [year, month] = p.on.split('-').map(Number);
    return monthLabel(year ?? 0, month ?? 1, 'short');
  };
  // One known point draws no line: the table alone says it.
  if (known.length < 2 || asTable) return <CurveTable points={data.points} label={label} real={real} />;
  return <CurveChart data={data} label={label} real={real} />;
}

function CurveTable({ points, label, real }: { points: NetWorthPointView[]; label: (p: NetWorthPointView, i: number) => string; real: boolean }) {
  const { t } = useTranslation();
  // Skip the months before the household had anything (no value, and no rate missing either).
  const first = Math.max(0, points.findIndex((p) => p.value || p.missingCurrency));
  return (
    <Table aria-label={t('dashboard.curve.table')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('dashboard.curve.month')}</TableHead>
          <TableHead scope="col" className="text-right">{t('dashboard.curve.value')}</TableHead>
          <TableHead scope="col" className="text-right">{t('dashboard.curve.contributions')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {points.map((p, i) => i < first ? null : <CurveRow key={p.on} point={p} name={label(p, i)} real={real} />)}
      </TableBody>
    </Table>
  );
}

function CurveRow({ point: p, name, real }: { point: NetWorthPointView; name: string; real: boolean }) {
  const { t } = useTranslation();
  const { value, paid } = reading(p, real);
  const reason = p.missingCurrency ? t('dashboard.curve.unknown_rate', { code: p.missingCurrency })
    : p.value ? t('dashboard.curve.real_unavailable') : t('dashboard.curve.no_value');
  return (
    <TableRow>
      <TableHead scope="row" className="font-normal">{name}</TableHead>
      {value && paid ? (
        <>
          <TableCell className="text-right"><Money value={value} tabular /></TableCell>
          <TableCell className="text-right"><Money value={paid} tabular /></TableCell>
        </>
      ) : (
        <TableCell colSpan={2} className="text-right text-muted-foreground">{reason}</TableCell>
      )}
    </TableRow>
  );
}

/** dataviz.md §2 and spec §7: an area for the value, a dashed line for the contributions, no dot on every point; an unknown month breaks both. */
function CurveChart({ data, label, real }: { data: NetWorthHistory; label: (p: NetWorthPointView, i: number) => string; real: boolean }) {
  const { t } = useTranslation();
  const gradient = `fill-${useId().replace(/:/g, '')}`;
  const config = {
    value: { label: t('dashboard.curve.value'), color: 'var(--chart-1)' },
    paid: { label: t('dashboard.curve.contributions'), color: 'var(--chart-2)' },
  } satisfies ChartConfig;
  const rows: Row[] = data.points.map((p, i) => {
    const { value, paid } = reading(p, real);
    return {
      on: p.on,
      label: label(p, i),
      value: value ? toPlot(value.amount) : null,
      paid: paid ? toPlot(paid.amount) : null,
      valueText: value ? formatMoney(value) : '',
      paidText: paid ? formatMoney(paid) : '',
    };
  });
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: data.currency, notation: 'compact' });
  return (
    <ChartContainer config={config} className="h-64 w-full">
      <ComposedChart data={rows} title={t('dashboard.curve.chart_title')} accessibilityLayer>
        <defs>
          <linearGradient id={gradient} x1="0" y1="0" x2="0" y2="1">
            <stop offset="5%" stopColor="var(--color-value)" stopOpacity={0.2} />
            <stop offset="95%" stopColor="var(--color-value)" stopOpacity={0} />
          </linearGradient>
        </defs>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="label" tickLine={false} axisLine={false} tickMargin={8} minTickGap={24} />
        <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
        <ChartTooltip content={<CurveTooltip />} />
        <ChartLegend content={<ChartLegendContent />} />
        <Area dataKey="value" type="monotone" stroke="var(--color-value)" strokeWidth={2} fill={`url(#${gradient})`} connectNulls={false} dot={false} activeDot={{ r: 4 }} animationDuration={800} />
        <Line dataKey="paid" stroke="var(--color-paid)" strokeWidth={2} strokeDasharray="5 4" type="stepAfter" connectNulls={false} dot={false} activeDot={{ r: 4 }} animationDuration={800} />
      </ComposedChart>
    </ChartContainer>
  );
}

function CurveTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row || !row.valueText) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{row.label}</div>
      <div className="tabular-nums">{t('dashboard.curve.value')} : {row.valueText}</div>
      <div className="tabular-nums">{t('dashboard.curve.contributions')} : {row.paidText}</div>
    </div>
  );
}
