import { Link, useNavigate } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useId, useState, type ReactNode } from 'react';
import { TriangleAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Bar, CartesianGrid, ComposedChart, Line, ReferenceLine, XAxis, YAxis } from 'recharts';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import type { PropertiesSearch, RootSearch } from '@/app/router';
import { SelectField } from '@/components/form/SelectField';
import { Segmented } from '@/components/form/Segmented';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney, Money, type MoneyValue } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { NEUTRAL_COLOR } from '@/components/ProviderIcon';
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '@/features/investments/chart/plot';
import { Direction } from '@/features/investments/Figures';
import { monthLabel } from '@/features/income/expected/months';
import { money, percent, signedMoney } from '@/features/loans/format';
import { PROPERTIES_RENTAL, type PropertiesRental, type RentalMonth, type RentalRow } from '../api';
import { TableToggle } from '@/components/page/TableToggle';
import '@/locales/properties';

type Scope = 'household' | 'me';

/** Beyond this many let properties, the filter is a select rather than buttons (spec §4.3). */
const BUTTONS = 3;
/** A lease ending this close is named under the timeline (spec §4.3). */
const ENDING_DAYS = 60;
const DAY = 86_400_000;

/** Properties dashboard §4.3: the let properties over the twelve months to today, as the server computed them; nothing computed here. */
export function RentalTab({ scope, property }: { scope: Scope; property?: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', PROPERTIES_RENTAL, { params: { query: { scope } } });
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const d = answer.data;
  if (!d) return <Skeleton role="status" aria-busy="true" aria-label={t('properties_rental.loading')} className="h-64 w-full" />;

  return (
    <div className="flex flex-col gap-6">
      <p className="text-sm text-muted-foreground">
        {t('properties_rental.period', { count: d.count, from: formatLocalDate(d.from, 'short'), to: formatLocalDate(d.to, 'short') })}
      </p>
      <Figures d={d} />
      <MonthsCard d={d} scope={scope} property={d.properties.some((p) => p.id === property) ? property : undefined} />
      <Rows d={d} />
      <Leases d={d} />
    </div>
  );
}

function Sub({ children }: { children: ReactNode }) {
  return <span className="block text-sm font-normal text-muted-foreground">{children}</span>;
}

const isZero = (amount: string) => /^-?0*(\.0*)?$/.test(amount);
const signed = (value: MoneyValue) => signedMoney(value.currency, value.amount);

function Figures({ d }: { d: PropertiesRental }) {
  const { t } = useTranslation();
  const unknown = t('properties_rental.unknown');
  const arrears = d.arrears;
  const owed = !!arrears && !isZero(arrears.amount) && !arrears.amount.startsWith('-');
  return (
    <StatLine items={[
      { label: t('properties_rental.received'), value: (
        <>{d.rentReceived ? formatMoney(d.rentReceived) : unknown}{d.expectedRent ? <Sub>{t('properties_rental.received_note', { amount: formatMoney(d.expectedRent) })}</Sub> : null}</>
      ) },
      { label: t('properties_rental.arrears'), value: (
        <>
          {owed ? <TriangleAlert aria-hidden="true" className="mr-1 inline size-4 text-status-serious" /> : null}
          {arrears ? formatMoney(arrears) : unknown}
          {arrears && isZero(arrears.amount) ? <Sub>{t('properties_rental.arrears_none')}</Sub> : null}
          {arrears?.amount.startsWith('-') ? <Sub>{t('properties_rental.arrears_over')}</Sub> : null}
        </>
      ) },
      { label: t('properties_rental.vacancy'), value: (
        <>{d.vacancyRate ? percent(t, d.vacancyRate) : t('properties_rental.dash')}{d.vacantDays !== null && d.vacantDays !== undefined ? <Sub>{t('properties_rental.vacancy_days', { count: d.vacantDays })}</Sub> : null}</>
      ) },
      { label: t('properties_rental.yield'), value: (
        <>
          {d.grossYield ? percent(t, d.grossYield) : t('properties_rental.not_significant')}
          <Sub>{d.netYieldBeforeTax ? t('properties_rental.yield_net', { rate: percent(t, d.netYieldBeforeTax) }) : t('properties_rental.not_significant_note')}</Sub>
          {d.grossYield && d.notSignificantCount > 0 ? <Sub>{t('properties_rental.not_significant_count', { count: d.notSignificantCount })}</Sub> : null}
        </>
      ) },
      { label: t('properties_rental.cash'), value: (
        <>{d.cashFlow ? <Direction amount={d.cashFlow} /> : unknown}{d.cashFlowMonthly ? <Sub>{t('properties_rental.cash_note', { amount: signed(d.cashFlowMonthly) })}</Sub> : null}</>
      ) },
    ]} />
  );
}

/** The months of one property when one is chosen (its own answer, §4.2), of all of them otherwise. */
function MonthsCard({ d, scope, property }: { d: PropertiesRental; scope: Scope; property?: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const filterId = useId();
  const [asTable, setAsTable] = useState(false);
  const one = $api.useQuery('get', PROPERTIES_RENTAL, { params: { query: { scope, property } } }, { enabled: !!property });
  const months = property ? one.data?.months : d.months;
  const choose = (next: string) =>
    void navigate({ to: '/properties', replace: true, resetScroll: false, search: (prev: RootSearch & PropertiesSearch) => ({ ...prev, property: next || undefined }) });
  const options = [{ value: '', label: t('properties_rental.all') }, ...d.properties.map((p) => ({ value: p.id, label: p.name }))];
  return (
    <SectionCard title={t('properties_rental.chart_title')} actions={
      <>
        {d.properties.length > 1 ? (
          d.properties.length <= BUTTONS ? (
            <>
              <span id={filterId} className="sr-only">{t('properties_rental.filter')}</span>
              <Segmented value={property ?? ''} onValueChange={choose} aria-labelledby={filterId} options={options} />
            </>
          ) : (
            <div className="w-56"><SelectField id={`${filterId}-select`} aria-label={t('properties_rental.filter')} value={property ?? ''} onValueChange={choose} options={options} /></div>
          )
        ) : null}
        <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
      </>
    }>
      {one.error ? <ProblemMessage problem={present(toProblem(one.error))} />
        : !months ? <Skeleton className="h-72 w-full" />
          : asTable ? <MonthsTable months={months} /> : <MonthsChart months={months} currency={d.currency} />}
    </SectionCard>
  );
}

const label = (month: string) => {
  const [year, m] = month.split('-').map(Number);
  return monthLabel(year ?? 0, m ?? 1, 'short');
};

/** A paid amount drawn below zero: its sign flipped as text, never computed (dataviz.md §1). */
const below = (amount: string) => toPlot(amount.startsWith('-') ? amount.slice(1) : `-${amount}`);

type Point = { label: string; rent: number | null; charges: number | null; loans: number | null; cash: number | null; month: RentalMonth };

function MonthsChart({ months, currency }: { months: RentalMonth[]; currency: string }) {
  const { t } = useTranslation();
  const rows: Point[] = months.map((m) => ({
    label: label(m.month), month: m,
    rent: m.rentReceived ? toPlot(m.rentReceived.amount) : null,
    charges: m.charges ? below(m.charges.amount) : null,
    loans: m.loanPayments ? below(m.loanPayments.amount) : null,
    cash: m.cashFlow ? toPlot(m.cashFlow.amount) : null,
  }));
  const config = {
    rent: { label: t('properties_rental.series_rent'), color: 'var(--chart-1)' },
    charges: { label: t('properties_rental.series_charges'), color: 'var(--chart-2)' },
    loans: { label: t('properties_rental.series_loans'), color: NEUTRAL_COLOR },
    cash: { label: t('properties_rental.series_cash'), color: 'var(--foreground)' },
  } satisfies ChartConfig;
  // Display of the plotting number only: the axis is a scale, not a figure the person reads exactly.
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency, notation: 'compact' });
  return (
    <ChartContainer config={config} className="h-72 w-full">
      <ComposedChart data={rows} maxBarSize={24} title={t('properties_rental.chart_title')} accessibilityLayer stackOffset="sign" margin={{ top: 8, right: 8 }}>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="label" tickLine={false} axisLine={false} tickMargin={8} />
        <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
        <ReferenceLine y={0} stroke="var(--border)" />
        <ChartTooltip content={<MonthTooltip />} />
        <ChartLegend content={<ChartLegendContent />} />
        <Bar dataKey="rent" stackId="month" fill="var(--color-rent)" stroke="var(--card)" strokeWidth={2} radius={[4, 4, 0, 0]} animationDuration={800} />
        <Bar dataKey="charges" stackId="month" fill="var(--color-charges)" stroke="var(--card)" strokeWidth={2} animationDuration={800} />
        <Bar dataKey="loans" stackId="month" fill="var(--color-loans)" stroke="var(--card)" strokeWidth={2} radius={[0, 0, 4, 4]} animationDuration={800} />
        <Line dataKey="cash" stroke="var(--color-cash)" strokeWidth={2} type="linear" dot={{ r: 3 }} connectNulls={false} animationDuration={800} />
      </ComposedChart>
    </ChartContainer>
  );
}

function MonthTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Point }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  const text = (value?: MoneyValue | null) => (value ? formatMoney(value) : t('properties_rental.unknown'));
  return (
    <div className="grid gap-1 rounded-xl border bg-background px-3 py-2 text-xs shadow-md">
      <div className="font-medium">{row.label}</div>
      <div className="tabular-nums">{t('properties_rental.series_rent')} : {text(row.month.rentReceived)}</div>
      <div className="tabular-nums">{t('properties_rental.series_charges')} : {text(row.month.charges)}</div>
      <div className="tabular-nums">{t('properties_rental.series_loans')} : {text(row.month.loanPayments)}</div>
      <div className="tabular-nums">{t('properties_rental.series_cash')} : {row.month.cashFlow ? signed(row.month.cashFlow) : text(null)}</div>
    </div>
  );
}

function MonthsTable({ months }: { months: RentalMonth[] }) {
  const { t } = useTranslation();
  const cell = (value?: MoneyValue | null) => (value ? <Money value={value} tabular /> : <span className="text-muted-foreground">{t('properties_rental.unknown')}</span>);
  return (
    <Table aria-label={t('properties_rental.chart_title')}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('properties_rental.month')}</TableHead>
          <TableHead scope="col" className="text-right">{t('properties_rental.series_rent')}</TableHead>
          <TableHead scope="col" className="text-right">{t('properties_rental.series_charges')}</TableHead>
          <TableHead scope="col" className="text-right">{t('properties_rental.series_loans')}</TableHead>
          <TableHead scope="col" className="text-right">{t('properties_rental.series_cash')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {months.map((m) => (
          <TableRow key={m.month}>
            <TableHead scope="row" className="font-normal">{label(m.month)}</TableHead>
            <TableCell className="text-right">{cell(m.rentReceived)}</TableCell>
            <TableCell className="text-right">{cell(m.charges)}</TableCell>
            <TableCell className="text-right">{cell(m.loanPayments)}</TableCell>
            <TableCell className="text-right tabular-nums">{m.cashFlow ? signed(m.cashFlow) : cell(null)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function leaseText(t: TFunction, r: RentalRow): string {
  const lease = r.currentTenancy;
  if (!lease) return t('properties_rental.no_lease');
  return lease.endsOn
    ? t('properties_rental.lease_until', { label: lease.label, date: formatLocalDate(lease.endsOn, 'short') })
    : t('properties_rental.lease_since', { label: lease.label, date: formatLocalDate(lease.startsOn, 'short') });
}

function Rows({ d }: { d: PropertiesRental }) {
  const { t } = useTranslation();
  const dash = <span aria-hidden="true">{t('properties_rental.dash')}</span>;
  const rate = (value?: string | null) => (value ? percent(t, value) : dash);
  const amount = (r: RentalRow, value?: string | null) => (value ? <span className="tabular-nums">{money(r.currency, value)}</span> : dash);
  return (
    <SectionCard title={t('properties_rental.table_title')}>
      <SortableTable<RentalRow> caption={t('properties_rental.table')} rows={d.properties} rowKey={(r) => r.id}
        rowLink={(r) => ({ to: '/properties/$id', params: { id: r.id }, search: { tab: 'rental' } })}
        columns={[
          { id: 'name', header: t('properties_rental.col_property'), sort: (r) => r.name, cell: (r) => <span className="font-medium">{r.name}</span> },
          { id: 'lease', header: t('properties_rental.col_lease'), cell: (r) => <span className="text-sm">{leaseText(t, r)}</span> },
          { id: 'rent', header: t('properties_rental.col_rent'), align: 'end', sort: (r) => amountKey(r.rentMonthly ?? null), cell: (r) => amount(r, r.rentMonthly) },
          { id: 'received', header: t('properties_rental.col_received'), align: 'end', sort: (r) => amountKey(r.rentReceived), cell: (r) => amount(r, r.rentReceived) },
          { id: 'arrears', header: t('properties_rental.col_arrears'), align: 'end', sort: (r) => amountKey(r.arrears),
            cell: (r) => (isZero(r.arrears) || r.arrears.startsWith('-')
              ? <>{t('properties_rental.up_to_date')}{r.arrears.startsWith('-') ? <Sub>{t('properties_rental.arrears_over')}</Sub> : null}</>
              : <span className="tabular-nums">{signedMoney(r.currency, r.arrears)}</span>) },
          { id: 'vacancy', header: t('properties_rental.col_vacancy'), align: 'end', sort: (r) => amountKey(r.vacancyRate ?? null), cell: (r) => rate(r.vacancyRate) },
          { id: 'gross', header: t('properties_rental.col_gross'), align: 'end', sort: (r) => amountKey(r.grossYield ?? null), cell: (r) => rate(r.grossYield) },
          { id: 'net', header: t('properties_rental.col_net'), align: 'end', sort: (r) => amountKey(r.netYieldBeforeTax ?? null), cell: (r) => rate(r.netYieldBeforeTax) },
          { id: 'cash', header: t('properties_rental.col_cash'), align: 'end', sort: (r) => amountKey(r.cashFlowConverted?.amount ?? null),
            cell: (r) => (r.cashFlow ? <span className="tabular-nums">{signedMoney(r.currency, r.cashFlow)}</span> : dash) },
        ]}
        mobile={(r) => (
          <div className="space-y-1 text-sm">
            <div className="flex justify-between gap-2"><span className="font-medium">{r.name}</span>{r.cashFlow ? <span className="tabular-nums">{signedMoney(r.currency, r.cashFlow)}</span> : null}</div>
            <div className="text-muted-foreground">{leaseText(t, r)}</div>
          </div>
        )} />
      <p className="flex flex-wrap justify-between gap-2 border-t pt-2 text-sm font-medium">
        <span>{t('properties_rental.total')}</span>
        <span className="tabular-nums">{[d.rentReceived ? formatMoney(d.rentReceived) : null, d.cashFlow ? signed(d.cashFlow) : null].filter(Boolean).join(' · ')}</span>
      </p>
      <p className="text-sm text-muted-foreground">{t('properties_rental.table_note')}</p>
    </SectionCard>
  );
}

/** Spec §4.3: a band per let property over three years, its leases full and the rest muted, a line for today; a table for assistive tech. */
function Leases({ d }: { d: PropertiesRental }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  // Positions on the timeline are dates, not amounts: the axis runs from three years ago to six months ahead.
  const end = Date.parse(d.to) + 183 * DAY;
  const start = Date.parse(d.to) - 3 * 365 * DAY;
  const at = (date: string) => Math.min(100, Math.max(0, ((Date.parse(date) - start) / (end - start)) * 100));
  const today = at(d.to);
  const names = new Map(d.properties.map((p) => [p.id, p.name]));
  const ending = d.tenancies.filter((l) => l.endsOn && Date.parse(l.endsOn) >= Date.parse(d.to) && Date.parse(l.endsOn) - Date.parse(d.to) <= ENDING_DAYS * DAY);
  return (
    <SectionCard title={t('properties_rental.leases_title')}>
      <div aria-hidden="true" className="space-y-3">
        {d.properties.map((p) => (
          <div key={p.id} className="grid grid-cols-[minmax(0,10rem)_1fr] items-center gap-3 text-sm">
            <span className="truncate">{p.name}</span>
            <div className="relative h-4 rounded bg-muted">
              {d.tenancies.filter((l) => l.propertyId === p.id).map((l) => (
                <span key={`${l.startsOn}/${l.label}`} className="absolute inset-y-0 rounded"
                  style={{ left: `${at(l.startsOn)}%`, width: `${(l.endsOn ? at(l.endsOn) : 100) - at(l.startsOn)}%`, background: 'var(--chart-1)' }} />
              ))}
              <span className="absolute -inset-y-1 w-px bg-foreground" style={{ left: `${today}%` }} />
            </div>
          </div>
        ))}
      </div>
      <Table className="sr-only" aria-label={t('properties_rental.leases_table')}>
        <TableHeader>
          <TableRow>
            <TableHead scope="col">{t('properties_rental.col_property')}</TableHead>
            <TableHead scope="col">{t('properties_rental.col_lease')}</TableHead>
            <TableHead scope="col">{t('properties_rental.lease_start')}</TableHead>
            <TableHead scope="col">{t('properties_rental.lease_end')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {d.tenancies.map((l) => (
            <TableRow key={`${l.propertyId}/${l.startsOn}/${l.label}`}>
              <TableCell>{names.get(l.propertyId) ?? ''}</TableCell>
              <TableCell>{l.label}</TableCell>
              <TableCell>{formatLocalDate(l.startsOn, 'short')}</TableCell>
              <TableCell>{l.endsOn ? formatLocalDate(l.endsOn, 'short') : t('properties_rental.lease_open')}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {ending.map((l) => (
        <p key={`${l.propertyId}/${l.startsOn}`} className="text-sm">
          {t('properties_rental.lease_ends_soon', { name: names.get(l.propertyId) ?? '', date: formatLocalDate(l.endsOn!, 'short') })}
        </p>
      ))}
      {capabilities.modules.includes('income') ? <Link to="/tax" className="text-sm hover:underline">{t('properties_rental.tax_link')}</Link> : null}
    </SectionCard>
  );
}
