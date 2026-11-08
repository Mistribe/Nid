import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatMoney, Money } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { TableToggle } from '@/components/page/TableToggle';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { monthLabel } from '@/lib/format';
import { HOUSEHOLD_INCOME, type PassiveIncome } from '@/api/investments';
import { toPlot } from '@/lib/plot';
import { kindText, missingRateCodes } from '../format';
import { incomeColor } from './colors';
import '@/locales/investments';
import '@/locales/forms';
import { OverviewSkeleton } from '@/components/page/skeletons';

type Row = PassiveIncome['rows'][number];
const month = (ym: string, style: 'long' | 'short' = 'long') => monthLabel(Number(ym.slice(0, 4)), Number(ym.slice(5, 7)), style);
/** A yield has no « + »: it is not a change. */
const pct = (value: number) => new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(value);
const isZero = (amount?: string) => !amount || /^-?0*(\.0*)?$/.test(amount);

/** Passive income (spec §4.1): twelve months of dividends, interest, distributions and net rent, from the server's figures. */
export default function IncomeTab({ scope }: { scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', HOUSEHOLD_INCOME, { params: { query: { scope } } });
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <OverviewSkeleton label={t('investments_income.loading')} />;
  return <Body d={answer.data} />;
}

function Body({ d }: { d: PassiveIncome }) {
  const { t } = useTranslation();
  const none = <span aria-hidden="true">—</span>;
  const kindLabel = (k: string) => t(`investments_income.kind.${k}`, { defaultValue: k });
  const names = d.excluded.map((e) => `${e.name} (${t(`investments_income.excluded_reason.${e.code}`, { defaultValue: e.code })})`).join(', ');
  const codes = missingRateCodes(d.findings);
  return (
    <div className="flex flex-col gap-6">
      <StatLine items={[
        { label: t('investments_income.total'), value: d.total ? formatMoney(d.total) : none },
        { label: t('investments_income.yield'), value: d.yieldPct == null ? none : t('investments.pct', { value: pct(d.yieldPct) }) },
        { label: t('investments_income.estimate'),
          value: <>{d.estimate.total ? formatMoney(d.estimate.total) : none}<span className="block text-sm font-normal text-muted-foreground">{t('investments_income.estimate_note')}</span></> },
        ...d.byKind.filter((k) => !isZero(k.amount?.amount)).map((k) => ({ label: kindLabel(k.kind), value: formatMoney(k.amount!) })),
      ]} />
      {d.rows.length === 0 ? <p className="text-muted-foreground">{t('investments_income.empty')}</p> : (
        <>
          <Months d={d} kindLabel={kindLabel} />
          <Rows d={d} kindLabel={kindLabel} />
        </>
      )}
      {codes ? <p className="text-sm text-muted-foreground">{t('investments_performance.unknown_rate', { codes })}</p> : null}
      {d.excluded.length > 0 ? <p className="text-sm text-muted-foreground">{t('investments_income.excluded', { names })}</p> : null}
    </div>
  );
}

type MonthRow = { month: string; label: string } & Record<string, number | string>;

/** Bars stacked by nature, a fixed colour each; the month in progress is named as partial; a table on demand. */
function Months({ d, kindLabel }: { d: PassiveIncome; kindLabel: (k: string) => string }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const kinds = d.byKind.map((k) => k.kind);
  const partial = d.months.find((m) => m.partial);
  const data: MonthRow[] = d.months.map((m) => ({
    month: m.month, label: month(m.month, 'short'),
    ...Object.fromEntries(m.byKind.map((k) => [k.kind, k.amount ? toPlot(k.amount.amount) : 0])),
  }));
  const config = Object.fromEntries(kinds.map((k) => [k, { label: kindLabel(k), color: incomeColor(k) }])) satisfies ChartConfig;
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: d.currency, notation: 'compact' });
  return (
    <SectionCard title={t('investments_income.months_title')} actions={<TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />}>
      {asTable ? (
        <Table aria-label={t('investments_income.months_table')}>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">{t('investments_income.month')}</TableHead>
              {kinds.map((k) => <TableHead key={k} scope="col" className="text-right">{kindLabel(k)}</TableHead>)}
            </TableRow>
          </TableHeader>
          <TableBody>
            {d.months.map((m) => (
              <TableRow key={m.month}>
                <TableHead scope="row" className="font-normal">{m.partial ? t('investments_income.partial_label', { month: month(m.month) }) : month(m.month)}</TableHead>
                {m.byKind.map((k) => <TableCell key={k.kind} className="text-right tabular-nums">{k.amount ? formatMoney(k.amount) : '—'}</TableCell>)}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : (
        <ChartContainer config={config} className="h-64 w-full">
          <BarChart data={data} stackOffset="sign" maxBarSize={24} title={t('investments_income.months_title')} accessibilityLayer margin={{ top: 8, right: 8 }}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="label" tickLine={false} axisLine={false} tickMargin={8} minTickGap={24} />
            <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
            <ChartTooltip cursor={false} content={<MonthTooltip d={d} kindLabel={kindLabel} />} />
            {kinds.map((k) => <Bar key={k} dataKey={k} stackId="income" fill={`var(--color-${k})`} stroke="var(--background)" strokeWidth={1} />)}
          </BarChart>
        </ChartContainer>
      )}
      {partial ? <p className="text-sm text-muted-foreground">{t('investments_income.partial', { month: month(partial.month) })}</p> : null}
    </SectionCard>
  );
}

/** The server's figures for the month, one line per nature that earned something. */
function MonthTooltip({ active, payload, d, kindLabel }: {
  active?: boolean; payload?: ReadonlyArray<{ payload?: MonthRow }>; d: PassiveIncome; kindLabel: (k: string) => string;
}) {
  const row = payload?.[0]?.payload;
  const m = d.months.find((x) => x.month === row?.month);
  if (!active || !m) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{month(m.month)}</div>
      {m.byKind.filter((k) => !isZero(k.amount?.amount)).map((k) => (
        <div key={k.kind} className="flex justify-between gap-4 tabular-nums"><span>{kindLabel(k.kind)}</span><span>{formatMoney(k.amount!)}</span></div>
      ))}
    </div>
  );
}

/** One line per envelope or let property, in the server's order, each to its own page. */
function Rows({ d, kindLabel }: { d: PassiveIncome; kindLabel: (k: string) => string }) {
  const { t } = useTranslation();
  const kind = (r: Row) => (r.source === 'property' ? t('investments_income.property') : kindText(t, r.kind));
  // Only the natures that earned something somewhere, in the server's order.
  const shown = d.byKind.map((k) => k.kind).filter((k) => d.rows.some((r) => !isZero(r.byKind.find((x) => x.kind === k)?.amount?.amount)));
  const amountOf = (r: Row, k: string) => r.byKind.find((x) => x.kind === k)?.amount;
  const yieldText = (r: Row) => (r.yieldPct == null ? '—' : t('investments.pct', { value: pct(r.yieldPct) }));
  return (
    <SortableTable<Row> caption={t('investments_income.rows_title')} rows={d.rows} rowKey={(r) => r.id}
      rowLink={(r) => (r.source === 'property' ? { to: '/properties/$id', params: { id: r.id } } : { to: '/investments/$id', params: { id: r.id } })}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (r) => r.name, cell: (r) => r.name },
        { id: 'kind', header: t('lists.col.kind'), sort: kind, cell: kind },
        ...shown.map((k) => ({ id: k, header: kindLabel(k), align: 'end' as const, sort: (r: Row) => amountKey(amountOf(r, k)?.amount),
          cell: (r: Row) => (isZero(amountOf(r, k)?.amount) ? <span aria-hidden="true">—</span> : formatMoney(amountOf(r, k)!)) })),
        { id: 'total', header: t('investments_income.col_total'), align: 'end', sort: (r) => amountKey(r.total?.amount),
          cell: (r) => r.total ? <Money value={r.total} tabular /> : <span className="text-muted-foreground">{t('investments_income.unknown')}</span> },
        { id: 'yield', header: t('investments_income.yield_short'), align: 'end', sort: (r) => r.yieldPct ?? Number.NaN, cell: yieldText },
      ]}
      mobile={(r) => (
        <div className="flex items-center justify-between gap-2">
          <span>{r.name}<span className="block text-sm text-muted-foreground">{kind(r)}</span></span>
          <span className="text-right tabular-nums">
            {r.total ? formatMoney(r.total) : t('investments_income.unknown')}
            {shown.filter((k) => !isZero(amountOf(r, k)?.amount)).map((k) => <span key={k} className="block text-sm text-muted-foreground">{kindLabel(k)} {formatMoney(amountOf(r, k)!)}</span>)}
          </span>
        </div>
      )} />
  );
}
