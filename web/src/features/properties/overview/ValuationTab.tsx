import { Link } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { Clock } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { CartesianGrid, Line, LineChart, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { NEUTRAL_COLOR } from '@/components/ProviderIcon';
import { ChartContainer, type ChartConfig } from '@/components/ui/chart';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '@/features/investments/chart/plot';
import { money, signedMoney } from '@/features/loans/format';
import { PROPERTIES_VALUATION, type PropertiesValuation, type ValuationRow } from '../api';
import { typeText } from '../format';
import '@/locales/properties';

type Scope = 'household' | 'me';

/** Properties dashboard §6.3: each property's value against its cost price, since its purchase; nothing computed here. */
export function ValuationTab({ scope }: { scope: Scope }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', PROPERTIES_VALUATION, { params: { query: { scope } } });
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const d = answer.data;
  if (!d) return <Skeleton role="status" aria-busy="true" aria-label={t('properties_valuation.loading')} className="h-64 w-full" />;

  return (
    <div className="flex flex-col gap-6">
      <Figures d={d} />
      <SectionCard title={t('properties_valuation.history_title')}>
        <div className="grid gap-6 md:grid-cols-2 xl:grid-cols-3">
          {d.properties.map((p) => <History key={p.id} p={p} to={lastDate(d)} />)}
        </div>
      </SectionCard>
      <Rows d={d} />
    </div>
  );
}

/** The latest estimate date of the household, or the newest purchase: the right end of each small multiple. */
const lastDate = (d: PropertiesValuation) =>
  [...d.properties.flatMap((p) => p.estimates.map((e) => e.on)), ...d.properties.map((p) => p.purchaseDate)].sort().at(-1) ?? '';

function Sub({ children }: { children: ReactNode }) {
  return <span className="block text-sm font-normal text-muted-foreground">{children}</span>;
}

const share = (t: TFunction, value: number) =>
  t('properties_valuation.pct', { value: new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1, signDisplay: 'exceptZero' }).format(value) });
/** A surface as the server wrote it, in French: « 82 », « 45,5 »; text only. */
const surfaceText = (value: string) => value.replace(/\.?0+$/, '').replace('.', ',');
const isNegative = (value?: MoneyValue | null) => !!value && value.amount.startsWith('-');

function Figures({ d }: { d: PropertiesValuation }) {
  const { t } = useTranslation();
  const unknown = t('properties_valuation.unknown');
  return (
    <StatLine items={[
      { label: t('properties_valuation.gain'), tone: d.latentGain ? (isNegative(d.latentGain) ? 'down' : 'up') : undefined, value: (
        <>
          {d.latentGain ? signedMoney(d.latentGain.currency, d.latentGain.amount) : unknown}
          <Sub>{d.latentGainPct !== null && d.latentGainPct !== undefined && d.costPrice
            ? t('properties_valuation.gain_note', { pct: share(t, d.latentGainPct), cost: formatMoney(d.costPrice) })
            : t('properties_valuation.gain_note_plain')}</Sub>
        </>
      ) },
      { label: t('properties_valuation.per_m2'), value: (
        <>
          {d.valuePerM2 ? formatMoney(d.valuePerM2) : t('properties_valuation.per_m2_none')}
          {d.surfaceM2 ? <Sub>{t('properties_valuation.per_m2_note', { surface: surfaceText(d.surfaceM2) })}</Sub> : null}
          {d.withoutSurfaceCount > 0 ? <Sub>{t('properties_valuation.without_surface', { count: d.withoutSurfaceCount })}</Sub> : null}
        </>
      ) },
      { label: t('properties_valuation.estimates'), value: (
        <>
          {t('properties_valuation.estimates_value', { count: d.upToDateCount, total: d.count })}
          {d.staleCount > 0 ? <Sub><span className="inline-flex items-center gap-1"><Clock aria-hidden="true" className="size-3.5" />{t('properties_valuation.stale', { count: d.staleCount })}</span></Sub> : null}
          {d.withoutValueCount > 0 ? <Sub>{t('properties_valuation.without_value', { count: d.withoutValueCount })}</Sub> : null}
        </>
      ) },
    ]} />
  );
}

type Point = { at: number; value: number | null; cost: number | null; estimated: boolean };

/** D18: the estimates as steps, the cost price dashed, on an axis from the purchase to the latest estimate; a table for assistive tech. */
function History({ p, to }: { p: ValuationRow; to: string }) {
  const { t } = useTranslation();
  const dates = [...new Set([...p.estimates.map((e) => e.on), ...p.costSteps.map((s) => s.on), to].filter((on) => on >= p.purchaseDate))].sort();
  // The last estimate and the last cost price on or before each date: a choice of the server's amounts, never a sum.
  const rows: Point[] = dates.map((on) => {
    const estimate = p.estimates.filter((e) => e.on <= on).at(-1);
    const step = p.costSteps.filter((s) => s.on <= on).at(-1);
    return { at: Date.parse(on), value: estimate ? toPlot(estimate.value) : null, cost: step ? toPlot(step.costPrice) : null, estimated: estimate?.on === on };
  });
  const config = {
    value: { label: t('properties_valuation.series_value'), color: 'var(--chart-1)' },
    cost: { label: t('properties_valuation.series_cost'), color: NEUTRAL_COLOR },
  } satisfies ChartConfig;
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: p.currency, notation: 'compact' });
  return (
    <figure className="space-y-1">
      <figcaption>
        <span className="block font-medium">{p.name}</span>
        <span className="block text-sm text-muted-foreground">{t('properties_valuation.bought', { type: typeText(t, p.type), date: formatLocalDate(p.purchaseDate, 'short') })}</span>
      </figcaption>
      {p.estimates.length === 0 ? <p className="text-sm text-muted-foreground">{t('properties_valuation.history_none')}</p> : (
        <ChartContainer config={config} className="h-40 w-full" aria-hidden="true">
          <LineChart data={rows} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="at" type="number" scale="time" domain={['dataMin', 'dataMax']} tickLine={false} axisLine={false}
              tickFormatter={(n: number) => String(new Date(n).getUTCFullYear())} />
            <YAxis width={56} tickLine={false} axisLine={false} domain={['auto', 'auto']} tickFormatter={(n: number) => axis.format(n)} />
            <Line dataKey="cost" type="stepAfter" stroke="var(--color-cost)" strokeWidth={1.5} strokeDasharray="4 3" dot={false} animationDuration={800} />
            {/* A dot on each estimate's own date: a single estimate makes no step, and would not show without it. */}
            <Line dataKey="value" type="stepAfter" stroke="var(--color-value)" strokeWidth={2} connectNulls={false} animationDuration={800}
              dot={({ cx, cy, index, payload }: { cx?: number; cy?: number; index?: number; payload?: Point }) => (payload?.estimated && cx != null && cy != null
                ? <circle key={index} cx={cx} cy={cy} r={3} fill="var(--color-value)" /> : <g key={index} />)} />
          </LineChart>
        </ChartContainer>
      )}
      <p className="text-sm">
        {p.value ? <span className="font-medium tabular-nums">{money(p.currency, p.value)}</span> : null}
        {p.latentGain ? <span className="ml-2 tabular-nums text-muted-foreground">{signedMoney(p.currency, p.latentGain)}</span> : null}
      </p>
      <Table className="sr-only" aria-label={t('properties_valuation.history_table', { name: p.name })}>
        <TableHeader>
          <TableRow>
            <TableHead scope="col">{t('properties_valuation.date')}</TableHead>
            <TableHead scope="col">{t('properties_valuation.series_value')}</TableHead>
            <TableHead scope="col">{t('properties_valuation.series_cost')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {dates.map((on) => {
            const estimate = p.estimates.find((e) => e.on === on);
            const step = p.costSteps.find((s) => s.on === on);
            return estimate || step ? (
              <TableRow key={on}>
                <TableCell>{formatLocalDate(on, 'short')}</TableCell>
                <TableCell>{estimate ? money(p.currency, estimate.value) : ''}</TableCell>
                <TableCell>{step ? money(p.currency, step.costPrice) : ''}</TableCell>
              </TableRow>
            ) : null;
          })}
        </TableBody>
      </Table>
    </figure>
  );
}

/** How long a property has been held: a gap of dates in years and months, not an amount (spec D1). */
function heldText(t: TFunction, purchased: string, to: Date): string {
  const [y, m, day] = purchased.split('-').map(Number);
  let months = (to.getFullYear() - (y ?? 0)) * 12 + (to.getMonth() + 1 - (m ?? 1));
  if (to.getDate() < (day ?? 1)) months -= 1;
  const years = Math.floor(Math.max(0, months) / 12);
  const rest = Math.max(0, months) % 12;
  if (years === 0) return t('properties_valuation.held_months', { count: rest });
  if (rest === 0) return t('properties_valuation.held_years', { count: years });
  return t('properties_valuation.held_and', { years: t('properties_valuation.held_years', { count: years }), months: t('properties_valuation.held_months', { count: rest }) });
}

function Rows({ d }: { d: PropertiesValuation }) {
  const { t } = useTranslation();
  const dash = <span aria-hidden="true">{t('properties_valuation.dash')}</span>;
  const now = new Date();
  const updated = (r: ValuationRow) => (
    <span className="inline-flex flex-col items-start gap-0.5">
      {r.valueOn ? (
        <span className="inline-flex items-center gap-1">
          {r.stale ? <Clock aria-hidden="true" className="size-4" /> : null}
          <LocalDate value={r.valueOn} style="short" />
          {r.stale ? <span className="sr-only">{t('properties_valuation.stale', { count: 1 })}</span> : null}
          <span className="text-muted-foreground">· {r.cadence === 'manual' ? t('properties_valuation.cadence_manual') : t('properties_valuation.cadence_yearly')}</span>
        </span>
      ) : <span className="text-muted-foreground">{t('properties_valuation.history_none')}</span>}
      {!r.valueOn || r.stale ? (
        <Link to="/properties/$id" params={{ id: r.id }} search={{ tab: 'valuations' }} className="text-sm hover:underline">{t('properties_valuation.add_estimate')}</Link>
      ) : null}
    </span>
  );
  return (
    <SectionCard title={t('properties_valuation.table_title')}>
      <SortableTable<ValuationRow> caption={t('properties_valuation.table')} rows={d.properties} rowKey={(r) => r.id}
        rowLink={(r) => ({ to: '/properties/$id', params: { id: r.id } })}
        columns={[
          { id: 'name', header: t('properties_valuation.col_property'), sort: (r) => r.name, cell: (r) => <span className="font-medium">{r.name}</span> },
          { id: 'surface', header: t('properties_valuation.col_surface'), align: 'end', sort: (r) => amountKey(r.surfaceM2 ?? null),
            cell: (r) => (r.surfaceM2 ? t('properties_valuation.surface', { value: surfaceText(r.surfaceM2) }) : dash) },
          { id: 'cost', header: t('properties_valuation.col_cost'), align: 'end', sort: (r) => amountKey(r.costPrice), cell: (r) => <span className="tabular-nums">{money(r.currency, r.costPrice)}</span> },
          { id: 'value', header: t('properties_valuation.col_value'), align: 'end', sort: (r) => amountKey(r.valueConverted?.amount ?? null),
            cell: (r) => (r.value ? <span className="tabular-nums">{money(r.currency, r.value)}</span> : dash) },
          { id: 'perm2', header: t('properties_valuation.col_per_m2'), align: 'end', sort: (r) => amountKey(r.valuePerM2 ?? null),
            cell: (r) => (r.valuePerM2 ? <span className="tabular-nums">{money(r.currency, r.valuePerM2)}</span> : dash) },
          { id: 'gain', header: t('properties_valuation.col_gain'), align: 'end', sort: (r) => amountKey(r.latentGain ?? null),
            cell: (r) => (r.latentGain ? (
              <span className="tabular-nums">
                {signedMoney(r.currency, r.latentGain)}
                {r.latentGainPct !== null && r.latentGainPct !== undefined ? <span className="text-muted-foreground"> · {share(t, r.latentGainPct)}</span> : null}
              </span>
            ) : dash) },
          { id: 'pace', header: t('properties_valuation.col_pace'), align: 'end', sort: (r) => r.annualPacePct ?? Number.NEGATIVE_INFINITY,
            cell: (r) => (r.annualPacePct === null || r.annualPacePct === undefined ? <span className="text-muted-foreground">{t('properties_valuation.pace_none')}</span> : share(t, r.annualPacePct)) },
          { id: 'held', header: t('properties_valuation.col_held'), sort: (r) => r.purchaseDate, cell: (r) => heldText(t, r.purchaseDate, now) },
          { id: 'updated', header: t('properties_valuation.col_updated'), sort: (r) => r.valueOn ?? '', cell: updated },
        ]}
        mobile={(r) => (
          <div className="space-y-1 text-sm">
            <div className="flex justify-between gap-2"><span className="font-medium">{r.name}</span>{r.value ? <span className="tabular-nums">{money(r.currency, r.value)}</span> : null}</div>
            <div className="text-muted-foreground">{r.latentGain ? signedMoney(r.currency, r.latentGain) : null}</div>
          </div>
        )} />
      <p className="text-sm text-muted-foreground">{t('properties_valuation.note')}</p>
    </SectionCard>
  );
}
