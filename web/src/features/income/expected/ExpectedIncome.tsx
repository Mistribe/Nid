import { useNavigate, useSearch } from '@tanstack/react-router';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { IncomeDetailSearch, RootSearch } from '@/app/router';
import { LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { toPlot } from '../../investments/chart/plot';
import { todayIso } from '../../subscriptions/today';
import { EXPECTED, type IncomeExpected, type IncomeSourceDetail } from '../api';
import { EXPECTED_PRESETS, expectedRange, monthLabel, type ExpectedPreset } from './months';

type Months = IncomeExpected['months'];
type Row = { key: string; label: string; longLabel: string; total: number; totalText: string };

/** Spec §5: the source's expected income month by month over a fixed choice of periods, as bars and as a table. */
export function ExpectedIncome({ source }: { source: IncomeSourceDetail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: RootSearch & IncomeDetailSearch = useSearch({ strict: false });
  const name = useId();
  // `strict: false` reads the raw search, so an unknown value is dropped here as well.
  const preset: ExpectedPreset = EXPECTED_PRESETS.find((p) => p === search.expected) ?? 'next12';
  const { from, to } = expectedRange(preset, todayIso());
  const answer = $api.useQuery('get', EXPECTED, { params: { path: { id: source.id }, query: { from, to } } });
  const choose = (next: ExpectedPreset) =>
    void navigate({
      to: '/income/$id', params: { id: source.id }, replace: true,
      search: (prev: RootSearch & IncomeDetailSearch) => ({ ...prev, expected: next === 'next12' ? undefined : next }),
    });

  return (
    <div className="space-y-3">
      <fieldset className="flex flex-wrap gap-3 text-sm">
        <legend className="sr-only">{t('income_detail.expected.period')}</legend>
        {EXPECTED_PRESETS.map((p) => (
          <label key={p} className="flex items-center gap-1">
            <input type="radio" name={name} value={p} checked={preset === p} onChange={() => choose(p)} />
            {t(`income_detail.expected.${p}`)}
          </label>
        ))}
      </fieldset>
      <Body data={answer.data} error={answer.error} />
    </div>
  );
}

function Body({ data, error }: { data?: IncomeExpected; error: unknown }) {
  const { t } = useTranslation();
  if (error) return <ProblemMessage problem={present(toProblem(error))} />;
  if (!data) return <div role="status" aria-busy="true" aria-label={t('income_detail.expected.loading')} className="h-64 animate-pulse rounded-md border bg-muted" />;
  if (data.months.every((m) => m.occurrences.length === 0 && m.oneOffs.length === 0))
    return <p className="text-muted-foreground">{t('income_detail.expected.empty')}</p>;
  return (
    <>
      <ExpectedChart months={data.months} />
      <ExpectedTable months={data.months} />
    </>
  );
}

/** One tone, one series, no legend (dataviz.md §6); every figure the person reads is the server's string. */
function ExpectedChart({ months }: { months: Months }) {
  const { t } = useTranslation();
  const currency = months[0]?.total.currency ?? 'EUR';
  const config = { total: { label: t('income_detail.expected.total'), color: 'var(--chart-1)' } } satisfies ChartConfig;
  const rows: Row[] = months.map((m) => ({
    key: `${m.year}-${m.month}`,
    label: monthLabel(m.year, m.month, 'short'),
    longLabel: monthLabel(m.year, m.month),
    total: toPlot(m.total.amount),
    totalText: formatMoney(m.total),
  }));
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  return (
    <ChartContainer config={config} className="h-64 w-full">
      <BarChart data={rows} title={t('income_detail.expected.chart_title')} accessibilityLayer>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="label" tickLine={false} tickMargin={8} />
        <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
        <ChartTooltip cursor={false} content={<MonthTooltip />} />
        <Bar dataKey="total" fill="var(--color-total)" radius={[4, 4, 0, 0]} isAnimationActive={false} />
      </BarChart>
    </ChartContainer>
  );
}

function MonthTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium">{row.longLabel}</div>
      <div className="tabular-nums">{row.totalText}</div>
    </div>
  );
}

/** One row group per month: its total, then each payment and each one-off. */
function ExpectedTable({ months }: { months: Months }) {
  const { t } = useTranslation();
  return (
    <table className="w-full text-sm" aria-label={t('income_detail.expected.table')}>
      <thead>
        <tr>
          <th scope="col" className="text-left">{t('income_detail.expected.col_month')}</th>
          <th scope="col" className="text-left">{t('income_detail.expected.col_line')}</th>
          <th scope="col" className="text-left">{t('income_detail.expected.col_date')}</th>
          <th scope="col" className="text-left">{t('income_detail.expected.col_amount')}</th>
        </tr>
      </thead>
      {months.map((m) => {
        const lines = m.occurrences.length + m.oneOffs.length; // a count of lines, not an amount
        return (
          <tbody key={`${m.year}-${m.month}`}>
            <tr>
              <th scope="rowgroup" rowSpan={Math.max(lines, 1) + 1} className="text-left align-top">{monthLabel(m.year, m.month)}</th>
              <td className="font-medium">{t('income_detail.expected.total')}</td>
              <td />
              <td className="font-medium"><Money value={m.total} tabular /></td>
            </tr>
            {m.occurrences.map((o) => (
              <tr key={`${o.phaseId}-${o.on}`}>
                <td>{o.label}</td>
                <td><LocalDate value={o.on} /></td>
                <td><Money value={o.amount} tabular /></td>
              </tr>
            ))}
            {m.oneOffs.map((f) => (
              <tr key={f.id}>
                <td>{f.note ? t('income_detail.expected.one_off_note', { note: f.note }) : t('income_detail.expected.one_off')}</td>
                <td><LocalDate value={f.occurredOn} /></td>
                <td><Money value={f.amount} tabular /></td>
              </tr>
            ))}
            {lines === 0 ? <tr><td colSpan={3} className="text-muted-foreground">{t('income_detail.expected.none')}</td></tr> : null}
          </tbody>
        );
      })}
    </table>
  );
}
