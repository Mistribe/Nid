import { Link } from '@tanstack/react-router';
import { Fragment, useId, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { Segmented } from '@/components/form/Segmented';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '@/features/investments/chart/plot';
import { monthLabel } from '@/features/income/expected/months';
import { money } from '@/features/loans/format';
import { fractionToPercent } from '@/features/loans/rate';
import { PROPERTIES_COSTS, type CostDue, type CostRow, type PropertiesCosts } from '../api';
import { categoryText } from '../format';
import { PropertyFilter } from './PropertyFilter';
import '@/locales/properties';

type Scope = 'household' | 'me';

/** Spec §5.3: the first dues shown, then « Tout voir ». */
const DUES_SHOWN = 8;

/** Properties dashboard §5.3: what the properties cost, paid and planned, and their real cost since purchase; nothing computed here. */
export function CostsTab({ scope, property }: { scope: Scope; property?: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', PROPERTIES_COSTS, { params: { query: { scope } } });
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const d = answer.data;
  if (!d) return <Skeleton role="status" aria-busy="true" aria-label={t('properties_costs.loading')} className="h-64 w-full" />;

  return (
    <div className="flex flex-col gap-6">
      <Figures d={d} />
      <div className="grid gap-4 md:grid-cols-2 [&>*]:min-w-0">
        <Categories d={d} />
        <Dues d={d} />
      </div>
      <Years d={d} scope={scope} property={d.properties.some((p) => p.id === property) ? property : undefined} />
      <Rows d={d} />
      <RealCost d={d} />
    </div>
  );
}

function Sub({ children }: { children: ReactNode }) {
  return <span className="block text-sm font-normal text-muted-foreground">{children}</span>;
}

function Figures({ d }: { d: PropertiesCosts }) {
  const { t } = useTranslation();
  const figure = (value?: MoneyValue | null) => (value ? formatMoney(value) : t('properties_costs.unknown'));
  const names = new Map(d.properties.map((p) => [p.id, p.name]));
  const next = d.dues[0];
  return (
    <StatLine items={[
      { label: t('properties_costs.paid'), value: <>{figure(d.paid)}{d.worksPaid ? <Sub>{t('properties_costs.paid_note', { amount: formatMoney(d.worksPaid) })}</Sub> : null}</> },
      { label: t('properties_costs.planned'), value: (
        <>{figure(d.planned)}{d.plannedMonthly ? <Sub>{t('properties_costs.planned_note', { amount: formatMoney(d.plannedMonthly) })}</Sub> : null}</>
      ) },
      { label: t('properties_costs.next'), value: next ? (
        <>
          {formatLocalDate(next.on, 'short')}
          <Sub>{[categoryText(t, next.category), names.get(next.propertyId), money(next.currency, next.amount)].filter(Boolean).join(' · ')}</Sub>
        </>
      ) : t('properties_costs.next_none') },
      { label: t('properties_costs.real_cost'), value: <>{figure(d.realCost)}<Sub>{t('properties_costs.real_cost_note')}</Sub></> },
    ]} />
  );
}

/** dataviz.md §2: one horizontal bar per category in the primary colour, its amount as a direct label; a table for the screen reader. */
function Categories({ d }: { d: PropertiesCosts }) {
  const { t } = useTranslation();
  const viewId = useId();
  const [view, setView] = useState<'paid' | 'planned'>('paid');
  const rows = d.byCategory.filter((c) => c[view]);
  // The longest bar sizes the others: a plotting number, never shown (dataviz.md §1).
  const longest = Math.max(1, ...rows.map((c) => toPlot(c[view]!.amount)));
  return (
    <SectionCard title={t('properties_costs.categories_title')} actions={
      <>
        <span id={viewId} className="sr-only">{t('properties_costs.categories_view')}</span>
        <Segmented value={view} onValueChange={(v) => setView(v === 'planned' ? 'planned' : 'paid')} aria-labelledby={viewId}
          options={[{ value: 'paid', label: t('properties_costs.view_paid') }, { value: 'planned', label: t('properties_costs.view_planned') }]} />
      </>
    }>
      {rows.length === 0 ? <p className="text-sm text-muted-foreground">{t('properties_costs.categories_empty')}</p> : (
        <Table aria-label={t('properties_costs.categories_table')}>
          <TableBody>
            {rows.map((c) => (
              <TableRow key={c.category}>
                <TableHead scope="row" className="h-auto w-full py-1.5 font-normal text-foreground">
                  <span className="block">{categoryText(t, c.category)}</span>
                  <span aria-hidden="true" className="mt-1 block h-2.5 rounded-sm" style={{ width: `${(toPlot(c[view]!.amount) / longest) * 100}%`, background: 'var(--chart-1)' }} />
                </TableHead>
                <TableCell className="py-1.5 text-right align-top tabular-nums">{formatMoney(c[view]!)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </SectionCard>
  );
}

const monthOf = (on: string) => on.slice(0, 7);

/** Spec §5.3: the occurrences of the planned costs, grouped by month, the first eight then « Tout voir ». */
function Dues({ d }: { d: PropertiesCosts }) {
  const { t } = useTranslation();
  const [all, setAll] = useState(false);
  const names = new Map(d.properties.map((p) => [p.id, p.name]));
  const shown = all ? d.dues : d.dues.slice(0, DUES_SHOWN);
  const groups: { month: string; dues: CostDue[] }[] = [];
  for (const due of shown) {
    const last = groups.at(-1);
    if (last && last.month === monthOf(due.on)) last.dues.push(due);
    else groups.push({ month: monthOf(due.on), dues: [due] });
  }
  const first = d.properties[0];
  return (
    <SectionCard title={t('properties_costs.dues_title')}>
      {d.dues.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          {t('properties_costs.dues_empty')}{' '}
          {first ? <Link to="/properties/$id" params={{ id: first.id }} search={{ tab: 'expenses' }} className="hover:underline">{t('properties_costs.dues_add')}</Link> : null}
        </p>
      ) : (
        <>
          <Table aria-label={t('properties_costs.dues_table')}>
            <TableHeader className="sr-only">
              <TableRow>
                <TableHead scope="col">{t('properties_costs.col_date')}</TableHead>
                <TableHead scope="col">{t('properties_costs.col_property')}</TableHead>
                <TableHead scope="col">{t('properties_costs.col_amount')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {groups.map((g) => {
                const [year, month] = g.month.split('-').map(Number);
                return (
                  <Fragment key={g.month}>
                    <TableRow>
                      <TableHead scope="colgroup" colSpan={3} className="h-auto pt-3 pb-1 text-xs font-normal text-muted-foreground">{monthLabel(year ?? 0, month ?? 1, 'long')}</TableHead>
                    </TableRow>
                    {g.dues.map((due) => (
                      <TableRow key={`${due.on}/${due.propertyId}/${due.category}`}>
                        {/* Wrapping, so a long property name does not widen a phone's page. */}
                        <TableCell className="py-1.5 whitespace-normal">
                          <span className="whitespace-nowrap">{formatLocalDate(due.on, 'short')}</span> · {categoryText(t, due.category)}
                        </TableCell>
                        <TableCell className="py-1.5 whitespace-normal break-words text-muted-foreground">{names.get(due.propertyId) ?? ''}</TableCell>
                        <TableCell className="py-1.5 text-right tabular-nums">{money(due.currency, due.amount)}</TableCell>
                      </TableRow>
                    ))}
                  </Fragment>
                );
              })}
            </TableBody>
          </Table>
          {d.dues.length > DUES_SHOWN ? (
            <Button type="button" variant="link" className="h-auto p-0" onClick={() => setAll((v) => !v)}>
              {all ? t('properties_costs.dues_less') : t('properties_costs.dues_all', { count: d.dues.length })}
            </Button>
          ) : null}
        </>
      )}
      <p className="text-sm text-muted-foreground">{t('properties_costs.dues_note')}</p>
    </SectionCard>
  );
}

/** Issue #405: the expenses paid per category and calendar year, of every property or one; the server's sums and changes. */
function Years({ d, scope, property }: { d: PropertiesCosts; scope: Scope; property?: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const one = $api.useQuery('get', PROPERTIES_COSTS, { params: { query: { scope, property } } }, { enabled: !!property });
  const years = property ? one.data?.byYear : d.byYear;
  const dash = <span aria-hidden="true">{t('properties_costs.dash')}</span>;
  const change = (c: string) => t('properties_costs.change', { rate: `${c.startsWith('-') ? '' : '+'}${fractionToPercent(c)}` });
  return (
    <SectionCard title={t('properties_costs.years_title')} actions={<PropertyFilter properties={d.properties} property={property} />}>
      {one.error ? <ProblemMessage problem={present(toProblem(one.error))} />
        : !years ? <Skeleton className="h-40 w-full" />
          : years.categories.length === 0 ? <p className="text-sm text-muted-foreground">{t('properties_costs.years_empty')}</p> : (
            <Table aria-label={t('properties_costs.years_table')}>
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">{t('properties_costs.col_category')}</TableHead>
                  {years.years.map((y, i) => (
                    <TableHead key={y.year} scope="col" className="text-right">
                      {y.year}
                      {y.complete ? null : <span className="block text-xs font-normal text-muted-foreground">
                        {t(i === years.years.length - 1 ? 'properties_costs.year_current' : 'properties_costs.year_partial')}
                      </span>}
                    </TableHead>
                  ))}
                </TableRow>
              </TableHeader>
              <TableBody>
                {years.categories.map((c) => (
                  <TableRow key={c.category}>
                    <TableHead scope="row" className="font-normal text-foreground">{categoryText(t, c.category)}</TableHead>
                    {c.cells.map((cell, i) => (
                      <TableCell key={years.years[i]?.year ?? i} className="text-right align-top tabular-nums">
                        {cell.paid ? formatMoney(cell.paid) : dash}
                        {cell.change ? <span className="block text-xs text-muted-foreground">{change(cell.change)}</span> : null}
                      </TableCell>
                    ))}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
      <p className="text-sm text-muted-foreground">{t('properties_costs.years_note')}</p>
    </SectionCard>
  );
}

function Rows({ d }: { d: PropertiesCosts }) {
  const { t } = useTranslation();
  const dash = <span aria-hidden="true">{t('properties_costs.dash')}</span>;
  const amount = (r: CostRow, value?: string | null) => (value && !/^0*(\.0*)?$/.test(value) ? <span className="tabular-nums">{money(r.currency, value)}</span> : dash);
  return (
    <SectionCard title={t('properties_costs.properties_title')}>
      <SortableTable<CostRow> caption={t('properties_costs.properties_table')} rows={d.properties} rowKey={(r) => r.id}
        rowLink={(r) => ({ to: '/properties/$id', params: { id: r.id }, search: { tab: 'expenses' } })}
        columns={[
          { id: 'name', header: t('properties_costs.col_property'), sort: (r) => r.name, cell: (r) => <span className="font-medium">{r.name}</span> },
          { id: 'paid', header: t('properties_costs.col_paid'), align: 'end', sort: (r) => amountKey(r.paid), cell: (r) => amount(r, r.paid) },
          { id: 'planned', header: t('properties_costs.col_planned'), align: 'end', sort: (r) => amountKey(r.plannedMonthly), cell: (r) => amount(r, r.plannedMonthly) },
          { id: 'rent', header: t('properties_costs.col_rent'), align: 'end', sort: (r) => amountKey(r.rentReceived), cell: (r) => amount(r, r.rentReceived) },
          { id: 'loan', header: t('properties_costs.col_loan'), align: 'end', sort: (r) => amountKey(r.loanPayment ?? null), cell: (r) => amount(r, r.loanPayment) },
        ]}
        mobile={(r) => (
          <div className="flex justify-between gap-2 text-sm"><span className="font-medium">{r.name}</span>{amount(r, r.paid)}</div>
        )} />
      <p className="flex flex-wrap justify-between gap-2 border-t pt-2 text-sm font-medium">
        <span>{t('properties_costs.total')}</span>
        <span className="tabular-nums">{[d.paid ? formatMoney(d.paid) : null, d.plannedMonthly ? formatMoney(d.plannedMonthly) : null].filter(Boolean).join(' · ')}</span>
      </p>
    </SectionCard>
  );
}

/** D15: the property page's real cost, in the six columns the server grouped, with the rents and the net cost of rents. */
function RealCost({ d }: { d: PropertiesCosts }) {
  const { t } = useTranslation();
  const dash = <span aria-hidden="true">{t('properties_costs.dash')}</span>;
  const amount = (r: CostRow, value: string) => (/^0*(\.0*)?$/.test(value) ? dash : <span className="tabular-nums">{money(r.currency, value)}</span>);
  const column = (id: string, header: string, value: (r: CostRow) => string) =>
    ({ id, header, align: 'end' as const, sort: (r: CostRow) => amountKey(value(r)), cell: (r: CostRow) => amount(r, value(r)) });
  return (
    <SectionCard title={t('properties_costs.real_title')}>
      <SortableTable<CostRow> caption={t('properties_costs.real_table')} rows={d.properties} rowKey={(r) => r.id}
        rowLink={(r) => ({ to: '/properties/$id', params: { id: r.id } })}
        columns={[
          { id: 'name', header: t('properties_costs.col_property'), sort: (r) => r.name, cell: (r) => <span className="font-medium">{r.name}</span> },
          column('acquisition', t('properties_costs.col_acquisition'), (r) => r.acquisition),
          column('works', t('properties_costs.col_works'), (r) => r.works),
          column('interest', t('properties_costs.col_interest'), (r) => r.loanInterest),
          column('insurance', t('properties_costs.col_insurance'), (r) => r.loanInsurance),
          column('fees', t('properties_costs.col_fees'), (r) => r.loanFees),
          column('other', t('properties_costs.col_other'), (r) => r.otherCharges),
          column('real', t('properties_costs.col_real'), (r) => r.realCost),
          column('rents', t('properties_costs.col_rents'), (r) => r.rentsSincePurchase),
          column('net', t('properties_costs.col_net'), (r) => r.netCostOfRents),
        ]}
        mobile={(r) => (
          <div className="flex justify-between gap-2 text-sm"><span className="font-medium">{r.name}</span><span className="tabular-nums">{money(r.currency, r.realCost)}</span></div>
        )} />
      <p className="flex flex-wrap justify-between gap-2 border-t pt-2 text-sm font-medium">
        <span>{t('properties_costs.total')}</span>
        <span className="tabular-nums">{d.realCost ? formatMoney(d.realCost) : dash}{d.netCostOfRents ? <> · {t('properties_costs.col_net')} {formatMoney(d.netCostOfRents)}</> : null}</span>
      </p>
      <p className="text-sm text-muted-foreground">{t('properties_costs.real_note')}</p>
    </SectionCard>
  );
}
