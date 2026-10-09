import { useNavigate, useSearch } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CartesianGrid, Line, LineChart, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { DashboardSearch, RootSearch } from '@/app/router';
import { formatMoney, Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { monthLabel } from '../income/expected/months';
import { toPlot } from '../investments/chart/plot';
import { NET_WORTH, type NetWorthHistory, type NetWorthPointView } from './api';

const PERIODS = ['12m', '3y', 'all'] as const;
type Period = (typeof PERIODS)[number];
type Row = { on: string; label: string; value: number | null; paid: number | null; valueText: string; paidText: string };

/** 2D2b spec §3: the household's value and cumulative contributions at each month end, then today. */
export function NetWorthCurve() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const name = useId();
  const search: RootSearch & DashboardSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const period: Period = search.curve === '3y' || search.curve === 'all' ? search.curve : '12m';
  const answer = $api.useQuery('get', NET_WORTH, { params: { query: { scope, period } } });
  const choose = (next: Period) =>
    void navigate({
      to: '/dashboard', replace: true,
      search: (prev: RootSearch & DashboardSearch) => ({ ...prev, curve: next === '12m' ? undefined : next }),
    });

  return (
    <div className="space-y-3">
      <fieldset className="flex flex-wrap gap-3 text-sm">
        <legend className="sr-only">{t('dashboard.curve.period')}</legend>
        {PERIODS.map((p) => (
          <label key={p} className="flex items-center gap-1">
            <input type="radio" name={name} value={p} checked={period === p} onChange={() => choose(p)} />
            {t(`dashboard.curve.period_${p}`)}
          </label>
        ))}
      </fieldset>
      <Body data={answer.data} error={answer.error} />
    </div>
  );
}

function Body({ data, error }: { data?: NetWorthHistory; error: unknown }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  if (!data && error) return <ProblemMessage problem={present(toProblem(error))} />;
  if (!data) return <div role="status" aria-busy="true" aria-label={t('dashboard.curve.loading')} className="h-64 animate-pulse rounded-md border bg-muted" />;
  const known = data.points.filter((p) => p.value);
  if (known.length === 0) return <p className="text-muted-foreground">{t('dashboard.curve.empty')}</p>;
  const label = (p: NetWorthPointView, i: number) => {
    if (i === data.points.length - 1) return t('dashboard.curve.today');
    const [year, month] = p.on.split('-').map(Number);
    return monthLabel(year ?? 0, month ?? 1, 'short');
  };
  // One known point draws no line: the table alone says it.
  if (known.length < 2) return <CurveTable points={data.points} label={label} />;
  return (
    <div className="space-y-2">
      <Button type="button" variant="outline" size="sm" onClick={() => setAsTable((v) => !v)}>
        {asTable ? t('envelope.show_chart') : t('envelope.show_table')}
      </Button>
      {asTable ? <CurveTable points={data.points} label={label} /> : <CurveChart data={data} label={label} />}
    </div>
  );
}

function CurveTable({ points, label }: { points: NetWorthPointView[]; label: (p: NetWorthPointView, i: number) => string }) {
  const { t } = useTranslation();
  // Skip the months before the household had anything (no value, and no rate missing either).
  const first = Math.max(0, points.findIndex((p) => p.value || p.missingCurrency));
  return (
    <table className="w-full text-sm" aria-label={t('dashboard.curve.table')}>
      <thead>
        <tr>
          <th scope="col" className="text-left">{t('dashboard.curve.month')}</th>
          <th scope="col" className="text-right">{t('dashboard.curve.value')}</th>
          <th scope="col" className="text-right">{t('dashboard.curve.contributions')}</th>
        </tr>
      </thead>
      <tbody>
        {points.map((p, i) => i < first ? null : (
          <tr key={p.on}>
            <th scope="row" className="text-left font-normal">{label(p, i)}</th>
            {p.value && p.contributions ? (
              <>
                <td className="text-right"><Money value={p.value} tabular /></td>
                <td className="text-right"><Money value={p.contributions} tabular /></td>
              </>
            ) : (
              <td colSpan={2} className="text-right text-muted-foreground">
                {p.missingCurrency ? t('dashboard.curve.unknown_rate', { code: p.missingCurrency }) : t('dashboard.curve.no_value')}
              </td>
            )}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** dataviz.md §2: two lines on one axis, a legend; an unknown month breaks both lines. */
function CurveChart({ data, label }: { data: NetWorthHistory; label: (p: NetWorthPointView, i: number) => string }) {
  const { t } = useTranslation();
  const config = {
    value: { label: t('dashboard.curve.value'), color: 'var(--chart-1)' },
    paid: { label: t('dashboard.curve.contributions'), color: 'var(--chart-2)' },
  } satisfies ChartConfig;
  const rows: Row[] = data.points.map((p, i) => ({
    on: p.on,
    label: label(p, i),
    value: p.value ? toPlot(p.value.amount) : null,
    paid: p.contributions ? toPlot(p.contributions.amount) : null,
    valueText: p.value ? formatMoney(p.value) : '',
    paidText: p.contributions ? formatMoney(p.contributions) : '',
  }));
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: data.currency, notation: 'compact' });
  return (
    <ChartContainer config={config} className="h-64 w-full">
      <LineChart data={rows} title={t('dashboard.curve.chart_title')} accessibilityLayer>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="label" tickLine={false} tickMargin={8} />
        <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
        <ChartTooltip content={<CurveTooltip />} />
        <ChartLegend content={<ChartLegendContent />} />
        <Line dataKey="value" stroke="var(--color-value)" strokeWidth={2} connectNulls={false} dot={rows.length <= 40 ? { r: 4, fill: 'var(--background)', strokeWidth: 2 } : false} isAnimationActive={false} />
        <Line dataKey="paid" stroke="var(--color-paid)" strokeWidth={2} type="stepAfter" connectNulls={false} dot={false} isAnimationActive={false} />
      </LineChart>
    </ChartContainer>
  );
}

function CurveTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row || !row.valueText) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium">{row.label}</div>
      <div className="tabular-nums">{t('dashboard.curve.value')} : {row.valueText}</div>
      <div className="tabular-nums">{t('dashboard.curve.contributions')} : {row.paidText}</div>
    </div>
  );
}
