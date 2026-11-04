import { Link } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { Clock } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { PropertiesSearch, RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { SectionCard } from '@/components/page/SectionCard';
import { tileOf } from '@/app/nav';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { Hero, HeroChange, HeroUnknown } from '@/components/page/Hero';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button, buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@/components/ui/table';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { toPlot } from '@/features/investments/chart/plot';
import { money, percent, signedMoney } from '@/features/loans/format';
import { PERSONS } from '@/features/subscriptions/api';
import { PROPERTIES_OVERVIEW, type PropertiesOverview, type PropertyOverviewRow, type PropertyWatch } from '../api';
import { typeText } from '../format';
import { typeColor } from './colors';
import { missingCodes, NetValueChart } from './NetValueChart';
import '@/locales/properties';
import '@/locales/forms';

type Scope = 'household' | 'me';
type Span = '12m' | '3y' | 'all';

/** Properties dashboard §3.3: the server's figures, then the curve, the split, what to watch and the properties; nothing computed here. */
export function OverviewTab({ scope, span }: { scope: Scope; span: Span }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', PROPERTIES_OVERVIEW, { params: { query: { scope, span } } });

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const d = answer.data;
  if (!d) return <Skeleton role="status" aria-busy="true" aria-label={t('properties_overview.loading')} className="h-64 w-full" />;
  if (d.count === 0)
    return (
      <EmptyState module="/properties" title={t('properties.empty_title')} body={t('properties.empty_body')}
        action={<Link to="/properties/new" className={buttonVariants()}>{t('properties.add')}</Link>} />
    );

  // DOM order is the phone's (figures, curve, watch, split, persons, properties).
  return (
    <TooltipProvider>
      <div className="flex flex-col gap-6">
        <NetHero d={d} />
        <Figures d={d} />
        <NetValueChart d={d} span={span} />
        <div className="grid gap-4 md:grid-cols-3 [&>*]:min-w-0">
          <div className="md:order-last"><WatchCard d={d} /></div>
          <Types d={d} />
          <Persons d={d} />
        </div>
        <Properties d={d} />
      </div>
    </TooltipProvider>
  );
}

/** « Inconnu » and why, for a figure a missing rate left out (D4). */
function useFigure(d: PropertiesOverview) {
  const { t } = useTranslation();
  const codes = missingCodes(d);
  return (value: MoneyValue | null | undefined): ReactNode =>
    value ? formatMoney(value)
      : <span>{t('properties_overview.unknown')}{codes ? <Sub>{t('properties_overview.unknown_rate', { codes })}</Sub> : null}</span>;
}

function Sub({ children }: { children: ReactNode }) {
  return <span className="block text-sm font-normal text-muted-foreground">{children}</span>;
}

/** A share the server rounded to 0.1 %, written as it is. */
const share = (t: TFunction, value: number) =>
  t('properties_overview.pct', { value: new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(value) });

const known = (value?: number | null): value is number => value !== null && value !== undefined;

/** The net value leads, with the latent gain and the server's points as its curve. */
function NetHero({ d }: { d: PropertiesOverview }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  return (
    <Hero label={t('properties_overview.net')} figure={d.netValue ? figure(d.netValue) : <HeroUnknown>{figure(d.netValue)}</HeroUnknown>}
      change={d.latentGain ? (
        <HeroChange amount={d.latentGain} pct={known(d.latentGainPct) ? share(t, d.latentGainPct) : null}>
          {t(known(d.latentGainPct) ? 'properties_overview.hero_gain' : 'properties_overview.hero_gain_plain')}
        </HeroChange>
      ) : <span className="flex flex-wrap gap-x-1"><span className="text-muted-foreground">{t('properties_overview.gain')}</span>{figure(d.latentGain)}</span>}
      note={known(d.netValuePct) ? t('properties_overview.net_note', { pct: share(t, d.netValuePct) }) : null}
      values={d.points.flatMap((p) => (p.netValue ? [toPlot(p.netValue.amount)] : []))} />
  );
}

function Figures({ d }: { d: PropertiesOverview }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  return (
    <StatLine items={[
      { label: t('properties_overview.value'), value: (
        <>
          {figure(d.value)}
          {d.staleCount > 0 ? <Sub><span className="inline-flex items-center gap-1"><Clock aria-hidden="true" className="size-3.5" />{t('properties_overview.stale', { count: d.staleCount })}</span></Sub> : null}
          {d.withoutValueCount > 0 ? <Sub>{t('properties_overview.without_value', { count: d.withoutValueCount })}</Sub> : null}
        </>
      ) },
      { label: t('properties_overview.balance'), value: (
        <>{figure(d.linkedBalance)}<Sub>{d.loanCount > 0 ? t('properties_overview.loans', { count: d.loanCount }) : t('properties_overview.no_loan')}</Sub></>
      ) },
      ...(d.rentReceived ? [{ label: t('properties_overview.rent'), value: (
        <>
          {formatMoney(d.rentReceived)}
          <Sub>{d.grossYield ? t('properties_overview.rent_note', { rate: percent(t, d.grossYield) }) : t('properties_overview.rent_note_plain')}</Sub>
        </>
      ) }] : []),
    ]} />
  );
}

/** dataviz.md §2: one stacked bar of the types (their fixed colour), then the table the person reads. */
function Types({ d }: { d: PropertiesOverview }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  if (d.byType.length === 0) return null;
  return (
    <SectionCard title={t('properties_overview.types_title')}>
      <div aria-hidden="true" className="flex h-3 gap-0.5 overflow-hidden rounded-full bg-muted">
        {d.byType.map((k) => <span key={k.type} style={{ width: `${k.sharePct ?? 0}%`, background: typeColor(k.type) }} />)}
      </div>
      <Table aria-label={t('properties_overview.types_table')}>
        <TableBody>
          {d.byType.map((k) => (
            <TableRow key={k.type}>
              <TableHead scope="row" className="h-auto py-1.5 font-normal whitespace-normal text-foreground">
                <span className="inline-flex items-center gap-2">
                  <span aria-hidden="true" className="size-2.5 shrink-0 rounded-sm" style={{ background: typeColor(k.type) }} />{typeText(t, k.type)}
                </span>
              </TableHead>
              <TableCell className="py-1.5 text-right tabular-nums">{figure(k.value)}</TableCell>
              <TableCell className="py-1.5 text-right tabular-nums text-muted-foreground">{k.sharePct === null || k.sharePct === undefined ? '' : share(t, k.sharePct)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </SectionCard>
  );
}

/** D5 and D8: each person's net value, in a neutral tone (the types own the colours here), named as the owner chips. */
function Persons({ d }: { d: PropertiesOverview }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const people = $api.useQuery('get', PERSONS);
  const figure = useFigure(d);
  if (d.byPerson.length === 0) return null;
  return (
    <SectionCard title={t('properties_overview.persons_title')} note={<span className="text-sm text-muted-foreground">{t('properties_overview.persons_note')}</span>}>
      <ul className="space-y-3">
        {d.byPerson.map((person, index) => {
          const name = people.data?.find((p) => p.id === person.personId)?.displayName ?? t('properties_overview.unknown_person', { n: index + 1 });
          return (
            <li key={person.personId} className="space-y-1 text-sm">
              <div className="flex justify-between gap-2"><span>{name}</span><span className="font-semibold tabular-nums">{figure(person.netValue)}</span></div>
              <div aria-hidden="true" className="h-2 rounded-full bg-muted">
                <div className="h-2 rounded-full bg-muted-foreground" style={{ width: `${Math.max(0, person.sharePct ?? 0)}%` }} />
              </div>
              <div className="text-muted-foreground tabular-nums">
                {[person.sharePct === null || person.sharePct === undefined ? null : share(t, person.sharePct),
                  person.value && person.linkedBalance
                    ? t('properties_overview.person_detail', { value: formatMoney(person.value), balance: formatMoney(person.linkedBalance) }) : null]
                  .filter(Boolean).join(' · ')}
              </div>
            </li>
          );
        })}
      </ul>
    </SectionCard>
  );
}

const WATCH_SHOWN = 4;
const WATCH_CODES = [
  'no_valuation', 'valuation_stale', 'rental_without_tenancy', 'visibility_mismatch', 'fx_missing', 'rent_arrears', 'property_cost_due', 'tenancy_end',
  'revision_due', 'revision_lost', 'irl_missing', 'revision_forbidden_dpe',
] as const;

/** The detail's tab where each code is dealt with (spec §3.3). */
const WATCH_TAB: Partial<Record<(typeof WATCH_CODES)[number], string>> = {
  no_valuation: 'valuations', valuation_stale: 'valuations', rental_without_tenancy: 'rental', rent_arrears: 'rental', tenancy_end: 'rental', revision_due: 'rental', revision_lost: 'rental',
  irl_missing: 'rental', revision_forbidden_dpe: 'rental',
  property_cost_due: 'expenses',
};

function WatchCard({ d }: { d: PropertiesOverview }) {
  const { t } = useTranslation();
  const [all, setAll] = useState(false);
  if (d.watch.length === 0) return null;
  const names = new Map(d.properties.map((p) => [p.id, p.name]));
  const shown = all ? d.watch : d.watch.slice(0, WATCH_SHOWN);
  return (
    <SectionCard title={t('properties_overview.watch_title')}>
      <GroupedList>
        {shown.map((w) => {
          const code = WATCH_CODES.find((c) => c === w.code);
          const tab = code ? WATCH_TAB[code] : undefined;
          return (
            <GroupedRow key={`${w.code}/${w.propertyId}/${w.loanId ?? ''}/${w.on ?? ''}`} tile={tileOf('/properties')}
              link={{ to: '/properties/$id', params: { id: w.propertyId }, search: tab ? { tab } : {} }}
              title={names.get(w.propertyId) ?? ''} detail={watchText(t, w)} />
          );
        })}
      </GroupedList>
      {d.watch.length > WATCH_SHOWN ? (
        <Button type="button" variant="link" className="h-auto p-0" onClick={() => setAll((v) => !v)}>
          {all ? t('properties_overview.watch_less') : t('properties_overview.watch_all', { count: d.watch.length })}
        </Button>
      ) : null}
    </SectionCard>
  );
}

function watchText(t: TFunction, w: PropertyWatch): string {
  const date = w.on ? formatLocalDate(w.on, 'short') : '';
  const amount = w.amount ? formatMoney(w.amount) : '';
  const code = WATCH_CODES.find((c) => c === w.code);
  return code ? t(`properties_overview.watch.${code}`, { date, amount }) : t('properties_overview.watch.other');
}

/** A property's amount in its own currency; in another currency than the household's, the converted one in a tooltip. */
function Amount({ currency, amount, converted }: { currency: string; amount: string; converted?: MoneyValue | null }) {
  const { t } = useTranslation();
  if (!converted || converted.currency === currency) return <span className="tabular-nums">{money(currency, amount)}</span>;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span tabIndex={0} className="tabular-nums underline decoration-dotted underline-offset-4">{money(currency, amount)}</span>
      </TooltipTrigger>
      <TooltipContent>{t('properties_overview.converted', { amount: formatMoney(converted) })}</TooltipContent>
    </Tooltip>
  );
}

function Properties({ d }: { d: PropertiesOverview }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  const dash = <span aria-hidden="true">{t('properties_overview.dash')}</span>;
  const name = (r: PropertyOverviewRow) => (
    <span className="inline-flex items-start gap-2">
      <span aria-hidden="true" className="mt-1.5 size-2.5 shrink-0 rounded-sm" style={{ background: typeColor(r.type) }} />
      <span className="inline-flex flex-col gap-0.5">
        <span className="font-medium">{r.name}</span>
        <span className="text-sm text-muted-foreground">{typeText(t, r.type)}</span>
      </span>
    </span>
  );
  const value = (r: PropertyOverviewRow) => r.value
    ? <Amount currency={r.currency} amount={r.value} converted={r.valueConverted} />
    : <span className="text-muted-foreground">{t('properties_overview.not_estimated')}</span>;
  const updated = (r: PropertyOverviewRow) => !r.valueOn ? dash : (
    <span className="inline-flex items-center gap-1">
      {r.stale ? <Clock aria-hidden="true" className="size-4" /> : null}
      <LocalDate value={r.valueOn} style="short" />
      {r.stale ? <span className="sr-only">{t('properties_overview.stale')}</span> : null}
    </span>
  );
  return (
    <SectionCard title={t('properties_overview.properties_title')} actions={
      <Link to="/properties" search={(prev: RootSearch & PropertiesSearch) => ({ ...prev, tab: 'list' })} className="text-sm hover:underline">
        {t('properties_overview.see_list')}
      </Link>
    }>
      <SortableTable<PropertyOverviewRow> caption={t('properties_overview.properties_list')} rows={d.properties} rowKey={(r) => r.id}
        rowLink={(r) => ({ to: '/properties/$id', params: { id: r.id } })}
        columns={[
          { id: 'name', header: t('properties_overview.col_property'), sort: (r) => r.name, cell: name },
          { id: 'owner', header: t('properties_overview.col_owners'), cell: (r) => <OwnerChips owners={r.owners} /> },
          { id: 'value', header: t('lists.col.value'), align: 'end', sort: (r) => amountKey(r.valueConverted?.amount ?? null), cell: value },
          { id: 'balance', header: t('properties_overview.col_balance'), align: 'end', sort: (r) => amountKey(r.linkedBalance),
            cell: (r) => (/^0*(\.0*)?$/.test(r.linkedBalance) ? dash : <span className="tabular-nums">{money(r.currency, r.linkedBalance)}</span>) },
          { id: 'net', header: t('properties_overview.col_net'), align: 'end', sort: (r) => amountKey(r.netValueConverted?.amount ?? null),
            cell: (r) => (r.netValue ? <Amount currency={r.currency} amount={r.netValue} converted={r.netValueConverted} /> : dash) },
          { id: 'gain', header: t('properties_overview.col_gain'), align: 'end', sort: (r) => amountKey(r.latentGain),
            cell: (r) => (r.latentGain ? <span className="tabular-nums">{signedMoney(r.currency, r.latentGain)}</span> : dash) },
          { id: 'yield', header: t('properties_overview.col_yield'), align: 'end', sort: (r) => amountKey(r.grossYield),
            cell: (r) => (r.grossYield ? percent(t, r.grossYield) : dash) },
          { id: 'updated', header: t('properties_overview.col_updated'), sort: (r) => r.valueOn ?? '', cell: updated },
        ]}
        mobile={(r) => (
          <div className="space-y-1">
            <div className="flex items-center justify-between gap-2">{name(r)}<OwnerChips owners={r.owners} /></div>
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
              {value(r)}
              {r.netValue ? <span className="text-muted-foreground">{t('properties_overview.col_net')} {money(r.currency, r.netValue)}</span> : null}
            </div>
          </div>
        )} />
      <p className="flex flex-wrap justify-between gap-2 border-t pt-2 text-sm font-medium">
        <span>{t('properties_overview.total_row', { count: d.count })}</span>
        <span className="tabular-nums">{[figure(d.value), d.netValue ? formatMoney(d.netValue) : null].filter(Boolean).map((x, i) => <span key={i}>{i > 0 ? ' · ' : ''}{x}</span>)}</span>
      </p>
    </SectionCard>
  );
}
