import { keepPreviousData } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Area, CartesianGrid, ComposedChart, Line, ReferenceDot, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { DashboardSearch, ProjectionHorizon } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Segmented } from '@/components/form/Segmented';
import { SectionCard } from '@/components/page/SectionCard';
import { TableToggle } from '@/components/page/TableToggle';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { monthLabel } from '@/lib/format';
import { toPlot } from '@/lib/plot';
import { PROJECTION, type HouseholdPoint, type Projection, type ProjectionMilestone, type RangePoint } from '@/api/dashboard';
import { RangesSummary, RangesToggle, bandOf } from './ProjectionRanges';

const YEARS = ['5y', '10y', '20y', '30y'] as const;
const DEFAULT_HORIZON = '10y';

/** yyyy-mm-dd as jj/mm/aaaa (a string swap, nothing computed). */
const dayFirst = (iso: string) => iso.split('-').reverse().join('/');
/** A server percent as a French one: « 6.0 » reads « 6,0 ». */
const comma = (value: string) => value.replace('.', ',');
/** Server zeros (« 0 », « 0.00 », « -0 »): the string compare behind « nothing to project ». */
const ZERO = /^-?0+(\.0+)?$/;
const isYearEnd = (p: HouseholdPoint) => p.on.endsWith('-12-31');
const month = (yyyyMm: string) => {
  const [year, m] = yyyyMm.split('-').map(Number);
  return monthLabel(year ?? 0, m ?? 1, 'long');
};

type Row = { on: string; plot: number; text: string; marks: string[]; band?: [number, number]; median?: number; range?: { low: string; median: string; high: string } };

/** Spec §8: the household's projected net worth month by month, in today's money or current money; horizon and reading in the card header. */
export function ProjectionCurve() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const horizonId = useId();
  const readingId = useId();
  const [asTable, setAsTable] = useState(false);
  const search: RootSearch & DashboardSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const horizon = search.projection ?? DEFAULT_HORIZON;
  const real = search.real !== 0;
  const ranged = search.ranges === 1;
  // The default horizon is left out of the request, as of the URL; the previous answer stays while the next horizon loads, so
  // the retirements it offers do not blink. Ranges spec §4: the bands only when the person asks for them.
  const answer = $api.useQuery('get', PROJECTION, {
    params: { query: { scope, ...(search.projection ? { horizon: search.projection } : {}), ...(ranged ? { ranges: true } : {}) } },
  }, { placeholderData: keepPreviousData });
  // Spec §2.3: only the retirements the server accepts as a horizon.
  const retirements = answer.data?.retirementHorizons ?? [];
  const chooseHorizon = (next: string) => {
    const projection = next === DEFAULT_HORIZON ? undefined : (next as ProjectionHorizon);
    void navigate({ to: '/dashboard', replace: true, resetScroll: false, search: (prev: RootSearch & DashboardSearch) => ({ ...prev, projection }) });
  };
  const chooseReading = (next: string) => {
    void navigate({ to: '/dashboard', replace: true, resetScroll: false, search: (prev: RootSearch & DashboardSearch) => ({ ...prev, real: next === 'nominal' ? (0 as const) : undefined }) });
  };
  const chooseRanges = (on: boolean) => {
    void navigate({ to: '/dashboard', replace: true, resetScroll: false, search: (prev: RootSearch & DashboardSearch) => ({ ...prev, ranges: on ? (1 as const) : undefined }) });
  };
  const data = answer.data;
  if (data && data.points.every((p) => ZERO.test(p.nominal)) && data.milestones.length === 0) return null;

  return (
    <SectionCard title={t('dashboard.projection.title')} actions={
      <>
        <span id={horizonId} className="sr-only">{t('dashboard.projection.horizon')}</span>
        <Segmented value={horizon} onValueChange={chooseHorizon} aria-labelledby={horizonId}
          options={[
            ...YEARS.map((y) => ({ value: y, label: t('dashboard.projection.horizon_years', { count: Number.parseInt(y, 10) }) })),
            ...retirements.map((r) => ({ value: `retire:${r.person}`, label: t('dashboard.projection.retire', { name: r.name }) })),
          ]} />
        <span id={readingId} className="sr-only">{t('dashboard.projection.reading')}</span>
        <Segmented value={real ? 'real' : 'nominal'} onValueChange={chooseReading} aria-labelledby={readingId}
          options={[{ value: 'real', label: t('dashboard.projection.real') }, { value: 'nominal', label: t('dashboard.projection.nominal') }]} />
        <RangesToggle pressed={ranged} onPressedChange={chooseRanges} />
        <Button asChild variant="outline" size="sm"><Link to="/retirement">{t('nav.retirement')}</Link></Button>
        {data ? <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} /> : null}
      </>
    }>
      <Body data={data} error={answer.error} real={real} asTable={asTable} computing={ranged && !data?.ranges && answer.isFetching} />
    </SectionCard>
  );
}

function Body({ data, error, real, asTable, computing }: { data?: Projection; error: unknown; real: boolean; asTable: boolean; computing: boolean }) {
  const { t } = useTranslation();
  if (!data && error) return <ProblemMessage problem={present(toProblem(error))} />;
  if (!data) return <Skeleton role="status" aria-busy="true" aria-label={t('dashboard.projection.loading')} className="h-64 w-full rounded-xl" />;
  return (
    <div className="space-y-3">
      {asTable ? <ProjectionTable data={data} real={real} /> : <ProjectionChart data={data} real={real} />}
      {computing ? <p role="status" className="text-sm text-muted-foreground">{t('dashboard.projection.ranges_loading')}</p> : null}
      {data.ranges ? <RangesSummary ranges={data.ranges} assumptions={data.assumptions} /> : null}
      <Milestones milestones={data.milestones} />
      <Warnings data={data} />
      <Assumptions data={data} />
    </div>
  );
}

const reading = (p: HouseholdPoint, real: boolean) => (real ? p.real : p.nominal);
/** The point's parts in the chosen reading: the server sends both, nothing is deflated here. */
const parts = (p: HouseholdPoint, real: boolean) => (real
  ? { savings: p.realSavings, property: p.realProperty, debt: p.realDebt }
  : { savings: p.savings, property: p.property, debt: p.debt });

function ProjectionTable({ data, real }: { data: Projection; real: boolean }) {
  const { t } = useTranslation();
  const last = data.points.length - 1;
  const rows = data.points.flatMap((p, i) => (isYearEnd(p) || i === last ? [{ p, i }] : []));
  const money = (amount: string) => formatCurrency(amount, data.currency);
  // The bands share the points' grid: point i's band is ranges.points[i].
  const band = (i: number) => { const r = data.ranges?.points[i]; return r ? bandOf(r, real) : undefined; };
  return (
    <Table aria-label={t('dashboard.projection.table')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('dashboard.projection.year')}</TableHead>
          <TableHead scope="col" className="text-right">{t('dashboard.projection.value')}</TableHead>
          <TableHead scope="col" className="text-right">{t('dashboard.projection.savings')}</TableHead>
          <TableHead scope="col" className="text-right">{t('dashboard.projection.property')}</TableHead>
          <TableHead scope="col" className="text-right">{t('dashboard.projection.debt')}</TableHead>
          {data.ranges ? (
            <>
              <TableHead scope="col" className="text-right">{t('dashboard.projection.ranges_low')}</TableHead>
              <TableHead scope="col" className="text-right">{t('dashboard.projection.ranges_median')}</TableHead>
              <TableHead scope="col" className="text-right">{t('dashboard.projection.ranges_high')}</TableHead>
            </>
          ) : null}
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map(({ p, i }) => {
          const part = parts(p, real);
          const b = band(i);
          return (
            <TableRow key={p.on}>
              <TableHead scope="row" className="font-normal">{isYearEnd(p) ? p.on.slice(0, 4) : month(p.on.slice(0, 7))}</TableHead>
              <TableCell className="text-right tabular-nums">{money(reading(p, real))}</TableCell>
              <TableCell className="text-right tabular-nums">{money(part.savings)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(part.property)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(part.debt)}</TableCell>
              {b ? (
                <>
                  <TableCell className="text-right tabular-nums">{money(b.low)}</TableCell>
                  <TableCell className="text-right tabular-nums">{money(b.median)}</TableCell>
                  <TableCell className="text-right tabular-nums">{money(b.high)}</TableCell>
                </>
              ) : null}
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

/** The milestone label the person reads: its kind and the server's label (savings_exhausted carries none). */
function useMilestoneText() {
  const { t } = useTranslation();
  return (m: ProjectionMilestone) => t(`dashboard.projection.milestone_${m.kind}`, { label: m.label, defaultValue: m.label });
}

function Milestones({ milestones }: { milestones: ProjectionMilestone[] }) {
  const { t } = useTranslation();
  const text = useMilestoneText();
  if (milestones.length === 0) return null;
  return (
    <ul aria-label={t('dashboard.projection.milestones')} className="space-y-0.5 text-sm text-muted-foreground">
      {milestones.map((m) => (
        <li key={`${m.kind}-${m.on}-${m.item ?? m.label}`}>{t('dashboard.projection.milestone_line', { date: dayFirst(m.on), what: text(m) })}</li>
      ))}
    </ul>
  );
}

function Warnings({ data }: { data: Projection }) {
  const { t } = useTranslation();
  if (data.warnings.length === 0) return null;
  return (
    <ul className="space-y-0.5 text-sm text-status-warning-text">
      {data.warnings.map((w) => (
        <li key={`${w.code}-${w.detail}`}>
          {w.code === 'income_end_extended' ? t('dashboard.projection.warning_income_end_extended', { name: w.detail })
            : w.code === 'savings_exhausted' ? t('dashboard.projection.warning_savings_exhausted', { month: w.detail ? month(w.detail) : '' })
            : w.code === 'fx_missing' ? t('dashboard.projection.warning_fx_missing', { code: w.detail })
            : w.code}
        </li>
      ))}
    </ul>
  );
}

function Assumptions({ data }: { data: Projection }) {
  const { t } = useTranslation();
  const a = data.assumptions;
  return (
    <div className="space-y-1 text-sm text-muted-foreground">
      <p>
        {t('dashboard.projection.assumptions', {
          equity: comma(a.classRates.equity ?? '0'), inflation: comma(a.inflation), savings: formatCurrency(data.monthlySavings, data.currency),
        })}
        {' '}
        <Link to="/settings/projection" className="text-foreground underline underline-offset-2">{t('dashboard.projection.edit_assumptions')}</Link>
      </p>
      <p>{t('dashboard.projection.disclaimer')}</p>
    </div>
  );
}

/**
 * dataviz.md §2: one line, no dot on every point, a dot on each milestone; the x axis ticks once a year. Ranges spec §6: with the
 * bands, the 10th–90th percentile as a tinted area of the line's colour and the median as a dashed line.
 */
function ProjectionChart({ data, real }: { data: Projection; real: boolean }) {
  const { t } = useTranslation();
  const config = { plot: { label: t('dashboard.projection.value'), color: 'var(--chart-1)' } } satisfies ChartConfig;
  const text = useMilestoneText();
  const rows: Row[] = data.points.map((p, i) => {
    const r: RangePoint | undefined = data.ranges?.points[i];
    const b = r ? bandOf(r, real) : undefined;
    return {
      on: p.on, plot: toPlot(reading(p, real)), text: formatCurrency(reading(p, real), data.currency), marks: [],
      ...(b ? { band: [toPlot(b.low), toPlot(b.high)] as [number, number], median: toPlot(b.median), range: b } : {}),
    };
  });
  // A milestone sits on the first point at or after its date (ISO dates compare as strings).
  const dots = data.milestones.flatMap((m) => {
    const row = rows.find((r) => r.on >= m.on);
    if (!row) return [];
    row.marks.push(text(m));
    return [{ key: `${m.kind}-${m.on}-${m.item ?? m.label}`, on: row.on, plot: row.plot }];
  });
  const ticks = data.points.filter(isYearEnd).map((p) => p.on);
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: data.currency, notation: 'compact' });
  return (
    <div className="space-y-2">
    {data.ranges ? (
      <ul aria-label={t('dashboard.projection.ranges_legend')} className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
        <li className="flex items-center gap-1.5"><span aria-hidden="true" className="h-0.5 w-4 rounded bg-chart-1" />{t('dashboard.projection.value')}</li>
        <li className="flex items-center gap-1.5"><span aria-hidden="true" className="w-4 border-t-2 border-dashed border-chart-1" />{t('dashboard.projection.ranges_median')}</li>
        <li className="flex items-center gap-1.5"><span aria-hidden="true" className="h-3 w-4 rounded-sm bg-chart-1/20" />{t('dashboard.projection.ranges_band')}</li>
      </ul>
    ) : null}
    <ChartContainer config={config} className="h-64 w-full">
      <ComposedChart data={rows} title={t('dashboard.projection.chart_title')} accessibilityLayer>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="on" ticks={ticks} tickFormatter={(on: string) => on.slice(0, 4)} tickLine={false} axisLine={false} tickMargin={8} />
        <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
        <ChartTooltip content={<ProjectionTooltip currency={data.currency} />} />
        {data.ranges ? <Area dataKey="band" type="monotone" stroke="none" fill="var(--color-plot)" fillOpacity={0.2} isAnimationActive={false} /> : null}
        {data.ranges ? <Line dataKey="median" type="monotone" stroke="var(--color-plot)" strokeWidth={1.5} strokeDasharray="4 4" dot={false} activeDot={false} isAnimationActive={false} /> : null}
        <Line dataKey="plot" type="monotone" stroke="var(--color-plot)" strokeWidth={2} dot={false} activeDot={{ r: 4 }} animationDuration={800} />
        {dots.map((d) => <ReferenceDot key={d.key} x={d.on} y={d.plot} r={4} fill="var(--color-plot)" stroke="var(--background)" />)}
      </ComposedChart>
    </ChartContainer>
    </div>
  );
}

function ProjectionTooltip({ currency, active, payload }: { currency: string; active?: boolean; payload?: ReadonlyArray<{ payload?: Row }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{dayFirst(row.on)}</div>
      <div className="tabular-nums">{t('dashboard.projection.value')} : {row.text}</div>
      {row.range ? (
        <>
          <div className="tabular-nums">{t('dashboard.projection.ranges_median')} : {formatCurrency(row.range.median, currency)}</div>
          <div className="tabular-nums">{t('dashboard.projection.ranges_band')} : {formatCurrency(row.range.low, currency)} – {formatCurrency(row.range.high, currency)}</div>
        </>
      ) : null}
      {row.marks.map((m) => <div key={m}>{m}</div>)}
    </div>
  );
}
