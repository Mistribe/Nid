import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { ArrowDownRight, ArrowUpRight } from 'lucide-react';
import { useState, type FormEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Bar, BarChart, CartesianGrid, Cell, ReferenceLine, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { InvestmentsSearch, RootSearch } from '@/app/router';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { FormDialog } from '@/components/FormDialog';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { monthLabel } from '../../income/expected/months';
import { ISO_DATE } from '../../subscriptions/edits/amount';
import { todayIso } from '../../subscriptions/today';
import { HOUSEHOLD_PERFORMANCE, OVERVIEW, type HouseholdPerformance } from '../api';
import { toPlot } from '../chart/plot';
import { pct } from '../Figures';
import { presetRange } from '../period/periods';
import { kindColor } from './colors';
import { TableToggle } from '@/components/page/TableToggle';
import '@/locales/investments';
import '@/locales/forms';

type Scope = 'household' | 'me';
const PRESETS = ['ytd', 'last12', 'last36', 'all'] as const;
type Preset = (typeof PRESETS)[number] | 'custom';

/** The period in the URL (both dates or neither), « Cette année » when absent; « Tout » starts at the oldest value (spec §4.2). */
function usePeriod(scope: Scope) {
  const { $api } = useApi();
  const search: RootSearch & InvestmentsSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const today = todayIso();
  const first = $api.useQuery('get', OVERVIEW, { params: { query: { scope, span: '12m' } } }).data?.firstAsOf;
  const ranges: Partial<Record<(typeof PRESETS)[number], { from: string; to: string }>> = {
    ytd: presetRange('ytd', today), last12: presetRange('last12', today), last36: presetRange('last36', today),
    ...(first && first < today ? { all: { from: first, to: today } } : {}),
  };
  const range = search.from && search.to ? { from: search.from, to: search.to } : ranges.ytd!;
  const preset: Preset = PRESETS.find((p) => ranges[p]?.from === range.from && ranges[p]?.to === range.to) ?? 'custom';
  const choose = (next: { from: string; to: string } | null) =>
    void navigate({ to: '/investments', replace: true, search: (prev: RootSearch & InvestmentsSearch) => ({ ...prev, from: next?.from, to: next?.to }) });
  return { today, range, preset, ranges, choose };
}

/** Investments dashboard §4.3: a period's performance, from the server's figures; nothing computed here. */
export function PerformanceTab({ scope }: { scope: Scope }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { today, range, preset, ranges, choose } = usePeriod(scope);
  const [custom, setCustom] = useState(false);
  const answer = $api.useQuery('get', HOUSEHOLD_PERFORMANCE, { params: { query: { scope, from: range.from, to: range.to } } });
  const d = answer.data;

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <Segmented value={preset} aria-label={t('investments_performance.period')}
          onValueChange={(next) => choose(next === 'ytd' ? null : ranges[next as (typeof PRESETS)[number]] ?? null)}
          options={PRESETS.filter((p) => ranges[p]).map((p) => ({ value: p, label: t(`investments_performance.${p}`) }))} />
        <Button type="button" variant="outline" size="sm" aria-pressed={preset === 'custom'} onClick={() => setCustom(true)}>{t('investments_performance.custom')}</Button>
        <span className="text-sm text-muted-foreground">
          {t('investments_performance.range', { from: formatLocalDate(range.from, 'short'), to: formatLocalDate(range.to, 'short') })}
        </span>
      </div>
      {custom ? <CustomPeriod today={today} range={range} onClose={() => setCustom(false)} onApply={(next) => { setCustom(false); choose(next); }} /> : null}
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !d ? <Skeleton role="status" aria-busy="true" aria-label={t('investments_performance.loading')} className="h-64 w-full" />
          : <Body d={d} />}
    </div>
  );
}

/** Two dates checked before anything is sent (start before end, none after today), as the envelope's own period. */
function CustomPeriod({ today, range, onClose, onApply }: {
  today: string; range: { from: string; to: string }; onClose: () => void; onApply: (next: { from: string; to: string }) => void;
}) {
  const { t } = useTranslation();
  const [from, setFrom] = useState(range.from);
  const [to, setTo] = useState(range.to);
  const [problem, setProblem] = useState<'from' | 'to' | 'from_future' | 'to_future' | 'reversed' | null>(null);
  const submit = (ev: FormEvent) => {
    ev.preventDefault();
    if (!ISO_DATE.test(from)) return setProblem('from');
    if (!ISO_DATE.test(to)) return setProblem('to');
    if (from > today) return setProblem('from_future');
    if (to > today) return setProblem('to_future');
    if (from >= to) return setProblem('reversed');
    onApply({ from, to });
  };
  return (
    <FormDialog title={t('investments_performance.custom_title')} onClose={onClose} onDone={onClose}>
      {({ titleId }) => (
        <form aria-labelledby={titleId} onSubmit={submit} noValidate className="space-y-4">
          <Field label={t('investments_performance.from')} error={problem === 'from' ? t('edit.required') : problem === 'from_future' ? t('field_error.core_date_in_future') : undefined}>
            {(c) => <DateField {...c} max={today} value={from} onChange={setFrom} />}
          </Field>
          <Field label={t('investments_performance.to')} error={problem === 'to' ? t('edit.required') : problem === 'to_future' ? t('field_error.core_date_in_future')
            : problem === 'reversed' ? t('investments_performance.reversed') : undefined}>
            {(c) => <DateField {...c} max={today} value={to} onChange={setTo} />}
          </Field>
          <div className="flex justify-end"><Button type="submit">{t('investments_performance.apply')}</Button></div>
        </form>
      )}
    </FormDialog>
  );
}

const signed = (value: MoneyValue) => (value.amount.startsWith('-') ? formatMoney(value) : `+${formatMoney(value)}`);
const negative = (value?: MoneyValue | null) => !!value && value.amount.startsWith('-') && !/^-0*(\.0*)?$/.test(value.amount);
const month = (ym: string, style: 'long' | 'short' = 'long') => monthLabel(Number(ym.slice(0, 4)), Number(ym.slice(5, 7)), style);

function useMissing(d: HouseholdPerformance) {
  const { t } = useTranslation();
  const codes = [...new Set(d.findings.filter((f) => f.code === 'core.fx_rate_missing').map((f) => (f.reference ?? '').split(':')[0]))].join(', ');
  return (value: MoneyValue | null | undefined, render: (v: MoneyValue) => ReactNode = formatMoney): ReactNode =>
    value ? render(value)
      : <span>{t('investments_performance.unknown')}{codes ? <span className="block text-sm font-normal text-muted-foreground">{t('investments_performance.unknown_rate', { codes })}</span> : null}</span>;
}

function Body({ d }: { d: HouseholdPerformance }) {
  const { t } = useTranslation();
  const figure = useMissing(d);
  const sub = (text: string) => <span className="block text-sm font-normal text-muted-foreground">{text}</span>;
  const monthOf = (ym?: string | null) => d.months.find((m) => m.month === ym);
  const best = monthOf(d.bestMonth);
  const worst = monthOf(d.worstMonth);
  return (
    <>
      <StatLine items={[
        { label: t('investments_performance.performance'), tone: d.performance ? (negative(d.performance) ? 'down' : 'up') : undefined,
          value: <>{figure(d.performance, signed)}{sub(t('investments_performance.performance_note'))}</> },
        { label: t('investments_performance.engaged'),
          value: <>{d.performancePct === null || d.performancePct === undefined ? '—' : t('investments.pct', { value: pct(d.performancePct) })}{sub(t('investments_performance.engaged_note'))}</> },
        ...(best?.performance ? [{ label: t('investments_performance.best'), value: <>{signed(best.performance)}{sub(month(best.month))}</> }] : []),
        ...(worst?.performance ? [{ label: t('investments_performance.worst'), value: <>{signed(worst.performance)}{sub(month(worst.month))}</> }] : []),
      ]} />
      <Excluded d={d} />
      {d.envelopes.length === 0 ? <p className="text-muted-foreground">{t('investments_performance.empty')}</p> : (
        <>
          <Bridge d={d} />
          <Months d={d} />
          <div className="grid gap-4 md:grid-cols-2">
            <Earned d={d} />
            <Returns d={d} />
          </div>
        </>
      )}
    </>
  );
}

function Excluded({ d }: { d: HouseholdPerformance }) {
  const { t } = useTranslation();
  if (d.excluded.length === 0) return null;
  const names = d.excluded.map((e) => `${e.name} (${t(`investments_performance.excluded_reason.${e.code === 'stale_start_value' ? 'stale_start_value' : 'no_start_value'}`)})`).join(', ');
  return <p className="text-sm text-muted-foreground">{t('investments_performance.excluded', { count: d.excluded.length, names })}</p>;
}

/** Spec §4.3: each change as a bar either side of a central axis, sized by `toPlot`; the sign is in the text, not only the colour. */
function Bridge({ d }: { d: HouseholdPerformance }) {
  const { t } = useTranslation();
  const figure = useMissing(d);
  const lines: { label: string; value?: MoneyValue | null; sign: 1 | -1; color: string; strong?: boolean }[] = [
    { label: t('investments_performance.contributions'), value: d.contributions, sign: 1, color: 'var(--primary)' },
    { label: t('investments_performance.withdrawals'), value: d.withdrawals, sign: -1, color: 'var(--muted-foreground)' },
    ...(d.transfers && !/^-?0*(\.0*)?$/.test(d.transfers.amount)
      ? [{ label: t('investments_performance.transfers'), value: d.transfers, sign: 1 as const, color: 'var(--muted-foreground)' }] : []),
    { label: t('investments_performance.gain'), value: d.performance, sign: 1, color: negative(d.performance) ? 'var(--destructive)' : 'var(--status-good)', strong: true },
  ];
  const size = (v?: MoneyValue | null) => (v ? Math.abs(toPlot(v.amount)) : 0);
  const max = Math.max(1, ...lines.map((l) => size(l.value)));
  const text = (l: (typeof lines)[number]) => (l.value ? (l.sign < 0 ? `−${formatMoney(l.value)}` : signed(l.value)) : null);
  return (
    <SectionCard title={t('investments_performance.bridge_title')}>
      <Table aria-label={t('investments_performance.bridge_table')}>
        <TableBody>
          <TableRow>
            <TableHead scope="row" className="font-normal text-foreground">{t('investments_performance.start', { date: formatLocalDate(d.from, 'short') })}</TableHead>
            <TableCell aria-hidden="true" />
            <TableCell className="text-right font-semibold tabular-nums">{figure(d.startValue)}</TableCell>
          </TableRow>
          {lines.map((l) => {
            const left = (l.sign < 0) !== negative(l.value);
            const width = `${(size(l.value) / max) * 50}%`;
            return (
              <TableRow key={l.label}>
                <TableHead scope="row" className={l.strong ? 'font-semibold text-foreground' : 'font-normal text-foreground'}>
                  <span className="inline-flex items-center gap-1">
                    {l.strong && l.value ? (negative(l.value) ? <ArrowDownRight aria-hidden="true" className="size-4" /> : <ArrowUpRight aria-hidden="true" className="size-4" />) : null}
                    {l.label}
                  </span>
                </TableHead>
                <TableCell aria-hidden="true" className="w-1/2 min-w-32">
                  <div className="relative h-5 rounded bg-muted/50">
                    <div className="absolute inset-y-0 left-1/2 w-px bg-border" />
                    <div className="absolute inset-y-0 rounded" style={{ background: l.color, width, ...(left ? { right: '50%' } : { left: '50%' }) }} />
                  </div>
                </TableCell>
                <TableCell className={`text-right tabular-nums ${l.strong ? 'font-semibold' : ''}`}>{text(l) ?? figure(l.value)}</TableCell>
              </TableRow>
            );
          })}
          <TableRow>
            <TableHead scope="row" className="font-normal text-foreground">{t('investments_performance.end', { date: formatLocalDate(d.to, 'short') })}</TableHead>
            <TableCell aria-hidden="true" />
            <TableCell className="text-right font-semibold tabular-nums">{figure(d.endValue)}</TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </SectionCard>
  );
}

type MonthRow = { month: string; label: string; value: number | null; text: string };

/** Spec §4.3: signed bars around zero (primary above, destructive below, the sign in the tooltip and the table); a table on demand. */
function Months({ d }: { d: HouseholdPerformance }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const rows: MonthRow[] = d.months.map((m) => ({
    month: m.month, label: month(m.month, 'short'), value: m.performance ? toPlot(m.performance.amount) : null, text: m.performance ? signed(m.performance) : t('investments_performance.unknown'),
  }));
  const config = { value: { label: t('investments_performance.gain'), color: 'var(--primary)' } } satisfies ChartConfig;
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: d.currency, notation: 'compact' });
  return (
    <SectionCard title={t('investments_performance.months_title')} actions={
      <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
    }>
      {asTable ? (
        <Table aria-label={t('investments_performance.months_table')}>
          <TableHeader><TableRow><TableHead scope="col">{t('investments_performance.month')}</TableHead><TableHead scope="col" className="text-right">{t('investments_performance.gain')}</TableHead></TableRow></TableHeader>
          <TableBody>
            {rows.map((r) => <TableRow key={r.month}><TableHead scope="row" className="font-normal">{month(r.month)}</TableHead><TableCell className="text-right tabular-nums">{r.text}</TableCell></TableRow>)}
          </TableBody>
        </Table>
      ) : (
        <ChartContainer config={config} className="h-64 w-full">
          <BarChart data={rows} maxBarSize={24} title={t('investments_performance.months_title')} accessibilityLayer margin={{ top: 8, right: 8 }}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="label" tickLine={false} axisLine={false} tickMargin={8} />
            <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
            <ReferenceLine y={0} stroke="var(--border)" />
            <ChartTooltip cursor={false} content={<MonthTooltip />} />
            <Bar dataKey="value" radius={2} animationDuration={800}>
              {rows.map((r) => <Cell key={r.month} fill={r.value !== null && r.value < 0 ? 'var(--destructive)' : 'var(--color-value)'} />)}
            </Bar>
          </BarChart>
        </ChartContainer>
      )}
      <p className="text-sm text-muted-foreground">{t('investments_performance.months_note')}</p>
    </SectionCard>
  );
}

function MonthTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: MonthRow }> }) {
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{month(row.month)}</div>
      <div className="tabular-nums">{row.text}</div>
    </div>
  );
}

/** Spec §4.3: one line per envelope, in the server's order, each to its envelope. */
function Earned({ d }: { d: HouseholdPerformance }) {
  const { t } = useTranslation();
  const max = Math.max(1, ...d.envelopes.map((e) => (e.performanceConverted ? Math.abs(toPlot(e.performanceConverted.amount)) : 0)));
  return (
    <SectionCard title={t('investments_performance.earned_title')}>
      <ul className="space-y-2 text-sm">
        {d.envelopes.map((e) => {
          const size = e.performanceConverted ? (Math.abs(toPlot(e.performanceConverted.amount)) / max) * 100 : 0;
          const down = negative(e.performanceConverted);
          return (
            <li key={e.id} className="grid grid-cols-[minmax(0,10rem)_1fr_auto] items-center gap-3">
              <Link to="/investments/$id" params={{ id: e.id }} className="inline-flex min-w-0 items-center gap-2 hover:underline">
                <span aria-hidden="true" className="size-2.5 shrink-0 rounded-sm" style={{ background: kindColor(e.kind) }} />
                <span className="truncate">{e.name}</span>
              </Link>
              <span aria-hidden="true" className="h-2.5 rounded-full bg-muted">
                <span className="block h-full rounded-full" style={{ width: `${size}%`, background: down ? 'var(--destructive)' : 'var(--primary)' }} />
              </span>
              <span className="text-right font-medium tabular-nums">{e.performanceConverted ? signed(e.performanceConverted) : t('investments_performance.unknown')}</span>
            </li>
          );
        })}
      </ul>
    </SectionCard>
  );
}

/** Spec §4.3: a dot per envelope on one axis, its value as a direct label, the savings accounts as a named dashed line (D15). */
function Returns({ d }: { d: HouseholdPerformance }) {
  const { t } = useTranslation();
  const meaningful = d.envelopes.filter((e) => e.annualReturnPct !== null && e.annualReturnPct !== undefined);
  const others = d.envelopes.filter((e) => e.annualReturnPct === null || e.annualReturnPct === undefined);
  const values = [...meaningful.map((e) => e.annualReturnPct!), ...(d.reference ? [d.reference.annualReturnPct] : []), 0];
  const low = Math.min(...values);
  const high = Math.max(...values);
  const at = (v: number) => (high === low ? 50 : ((v - low) / (high - low)) * 100);
  const percent = (v: number) => t('investments.pct', { value: pct(v) });
  return (
    <SectionCard title={t('investments_performance.returns_title')} note={d.reference ? <span className="text-sm text-muted-foreground">{t('investments_performance.returns_note')}</span> : undefined}>
      {meaningful.length === 0 ? <p className="text-sm text-muted-foreground">{t('investments_performance.returns_none')}</p> : (
        <ul className="relative space-y-3 pb-6 text-sm">
          {d.reference ? (
            <li aria-hidden="true" className="pointer-events-none absolute inset-y-0" style={{ left: `calc(9rem + (100% - 13rem) * ${at(d.reference.annualReturnPct) / 100})` }}>
              <span className="absolute inset-y-0 border-l-2 border-dashed border-[var(--chart-3)]" />
              <span className="absolute bottom-0 whitespace-nowrap pl-1 text-xs text-muted-foreground">{t('investments_performance.returns_reference', { pct: percent(d.reference.annualReturnPct) })}</span>
            </li>
          ) : null}
          {meaningful.map((e) => (
            <li key={e.id} className="grid grid-cols-[9rem_1fr_4rem] items-center gap-0">
              <span className="truncate pr-2">{e.name}</span>
              <span aria-hidden="true" className="relative h-4">
                <span className="absolute inset-x-0 top-1/2 h-px bg-border" />
                <span className="absolute top-1/2 size-3 -translate-x-1/2 -translate-y-1/2 rounded-full" style={{ left: `${at(e.annualReturnPct!)}%`, background: kindColor(e.kind) }} />
              </span>
              <span className="text-right tabular-nums">{percent(e.annualReturnPct!)}</span>
            </li>
          ))}
        </ul>
      )}
      {d.reference ? <p className="sr-only">{t('investments_performance.returns_reference', { pct: percent(d.reference.annualReturnPct) })}</p> : null}
      {others.length > 0 ? <p className="text-sm text-muted-foreground">{t('investments_performance.returns_not_meaningful', { names: others.map((e) => e.name).join(', ') })}</p> : null}
    </SectionCard>
  );
}
