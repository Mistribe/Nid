import { Link } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { Clock } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { InvestmentsSearch, RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, Money, type MoneyValue } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { SectionCard } from '@/components/page/SectionCard';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button, buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@/components/ui/table';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { PERSONS } from '@/features/subscriptions/api';
import { OVERVIEW, type EnvelopeOverviewRow, type InvestmentsOverview, type Watch } from '../api';
import { AnnualReturn, Direction, Gain, pct } from '../Figures';
import { kindText } from '../format';
import { kindColor } from './colors';
import { missingCodes, ValueChart } from './ValueChart';
import '@/locales/investments';
import '@/locales/forms';

type Scope = 'household' | 'me';
type Span = '12m' | '3y' | 'all';

/** Investments dashboard §3.3: the server's figures, then the curve, the split, what to watch and the envelopes; nothing computed here. */
export function OverviewTab({ scope, span }: { scope: Scope; span: Span }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', OVERVIEW, { params: { query: { scope, span } } });

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const d = answer.data;
  if (!d) return <Skeleton role="status" aria-busy="true" aria-label={t('investments_overview.loading')} className="h-64 w-full" />;
  if (d.openCount === 0 && d.closedCount === 0)
    return (
      <EmptyState title={t('investments.empty_title')} body={t('investments.empty_body')}
        action={<Link to="/investments/new" className={buttonVariants()}>{t('investments.add')}</Link>} />
    );
  if (d.openCount === 0)
    return (
      <EmptyState title={t('investments_overview.none_open_title')} body={t('investments_overview.none_open_body')}
        action={(
          <Link to="/investments" search={(prev: RootSearch & InvestmentsSearch) => ({ ...prev, tab: 'list', closed: 1 as const })}
            className={buttonVariants({ variant: 'outline' })}>{t('investments_overview.see_closed')}</Link>
        )} />
    );

  // DOM order is the phone's (figures, curve, watch, split, persons, envelopes).
  return (
    <TooltipProvider>
      <div className="flex flex-col gap-6">
        <Figures d={d} />
        <ValueChart d={d} span={span} />
        <div className="grid gap-4 md:grid-cols-3">
          <div className="md:order-last"><WatchCard d={d} /></div>
          <Kinds d={d} />
          <Persons d={d} />
        </div>
        <Envelopes d={d} />
      </div>
    </TooltipProvider>
  );
}

/** « Inconnu » and why, for a figure a missing rate left out (D4). */
function useFigure(d: InvestmentsOverview) {
  const { t } = useTranslation();
  const codes = missingCodes(d);
  return (value: MoneyValue | null | undefined): ReactNode =>
    value ? formatMoney(value)
      : <span>{t('investments_overview.unknown')}{codes ? <Sub>{t('investments_overview.unknown_rate', { codes })}</Sub> : null}</span>;
}

function Sub({ children }: { children: ReactNode }) {
  return <span className="block text-sm font-normal text-muted-foreground">{children}</span>;
}

/** A share the server rounded, written without a sign. */
const share = (value: number) => new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(value);

const signed = (value: MoneyValue) => (value.amount.startsWith('-') ? formatMoney(value) : `+${formatMoney(value)}`);

/** What an estimated envelope adds under its valuation's date: words, not colour. */
function Estimated({ on }: { on: string }) {
  const { t } = useTranslation();
  return <span className="block text-xs text-muted-foreground">{t('investments_overview.estimated_on', { date: formatLocalDate(on, 'short') })}</span>;
}

function Figures({ d }: { d: InvestmentsOverview }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  const percent = (value: number) => t('investments.pct', { value: pct(value) });
  return (
    <StatLine items={[
      { label: t('investments_overview.value'), value: (
        <>
          {figure(d.value)}
          {d.oldestAsOf ? <Sub>{t('investments_overview.value_as_of', { date: formatLocalDate(d.oldestAsOf, 'short') })}</Sub> : null}
          {d.staleCount > 0 ? <Sub><span className="inline-flex items-center gap-1"><Clock aria-hidden="true" className="size-3.5" />{t('investments_overview.stale', { count: d.staleCount })}</span></Sub> : null}
          {d.estimatedCount > 0 ? <Sub>{t('investments_overview.estimated_count', { count: d.estimatedCount })}</Sub> : null}
          {d.withoutValueCount > 0 ? <Sub>{t('investments_overview.without_value', { count: d.withoutValueCount })}</Sub> : null}
        </>
      ) },
      { label: t('investments_overview.net'), value: <>{figure(d.netContributions)}<Sub>{t('investments_overview.net_note')}</Sub></> },
      { label: t('investments_overview.gain'), value: (
        <>
          {d.gain ? <Direction amount={d.gain} /> : figure(d.gain)}
          <Sub>{d.gainPct === null || d.gainPct === undefined ? t('investments_overview.gain_note_plain') : t('investments_overview.gain_note', { pct: percent(d.gainPct) })}</Sub>
        </>
      ) },
      { label: t('investments_overview.annual'), value: (
        <>
          {d.annualReturnPct === null || d.annualReturnPct === undefined ? t('investments_overview.not_meaningful') : <AnnualReturn value={d.annualReturnPct} />}
          <Sub>{t('investments_overview.annual_note')}</Sub>
        </>
      ) },
      ...(d.sinceJanuary ? [{ label: t('investments_overview.since'), value: (
        <>
          <Direction amount={d.sinceJanuary} />
          <Sub>{d.sinceJanuaryPct === null || d.sinceJanuaryPct === undefined ? t('investments_overview.since_note_plain') : t('investments_overview.since_note', { pct: percent(d.sinceJanuaryPct) })}</Sub>
        </>
      ) }] : []),
    ]} />
  );
}

/** dataviz.md §2: one stacked bar of the kinds (their fixed colour), then the table the person reads. */
function Kinds({ d }: { d: InvestmentsOverview }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  return (
    <SectionCard title={t('investments_overview.kinds_title')}>
      <div aria-hidden="true" className="flex h-3 gap-0.5 overflow-hidden rounded-full bg-muted">
        {d.byKind.map((k) => <span key={k.kind} style={{ width: `${k.sharePct ?? 0}%`, background: kindColor(k.kind) }} />)}
      </div>
      <Table aria-label={t('investments_overview.kinds_table')}>
        <TableBody>
          {d.byKind.map((k) => (
            <TableRow key={k.kind}>
              <TableHead scope="row" className="h-auto py-1.5 font-normal text-foreground">
                <span className="inline-flex items-center gap-2">
                  <span aria-hidden="true" className="size-2.5 rounded-sm" style={{ background: kindColor(k.kind) }} />{kindText(t, k.kind)}
                </span>
              </TableHead>
              <TableCell className="py-1.5 text-right tabular-nums">{figure(k.value)}</TableCell>
              <TableCell className="py-1.5 text-right tabular-nums text-muted-foreground">{k.sharePct === null || k.sharePct === undefined ? '' : t('investments.pct', { value: share(k.sharePct) })}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </SectionCard>
  );
}

/** D7 and D10: each person's part, in a neutral tone (the kinds own the colours here), named as the owner chips. */
function Persons({ d }: { d: InvestmentsOverview }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const people = $api.useQuery('get', PERSONS);
  const figure = useFigure(d);
  if (d.byPerson.length === 0) return null;
  return (
    <SectionCard title={t('investments_overview.persons_title')} note={<span className="text-sm text-muted-foreground">{t('investments_overview.persons_note')}</span>}>
      <ul className="space-y-3">
        {d.byPerson.map((person, index) => {
          const name = people.data?.find((p) => p.id === person.personId)?.displayName ?? t('investments_overview.unknown_person', { n: index + 1 });
          return (
            <li key={person.personId} className="space-y-1 text-sm">
              <div className="flex justify-between gap-2"><span>{name}</span><span className="font-semibold tabular-nums">{figure(person.value)}</span></div>
              <div aria-hidden="true" className="h-2 rounded-full bg-muted">
                <div className="h-2 rounded-full bg-muted-foreground" style={{ width: `${person.sharePct ?? 0}%` }} />
              </div>
              <div className="text-muted-foreground tabular-nums">
                {[person.sharePct === null || person.sharePct === undefined ? null : t('investments.pct', { value: share(person.sharePct) }),
                  person.gain ? t('investments_overview.person_gain', { amount: signed(person.gain) }) : null].filter(Boolean).join(' · ')}
              </div>
            </li>
          );
        })}
      </ul>
    </SectionCard>
  );
}

const WATCH_SHOWN = 4;
const WATCH_CODES = ['stale_value', 'no_value', 'missing_opening', 'holding_maturity', 'uc_share_below_floor'] as const;

function WatchCard({ d }: { d: InvestmentsOverview }) {
  const { t } = useTranslation();
  const [all, setAll] = useState(false);
  if (d.watch.length === 0) return null;
  const names = new Map(d.envelopes.map((e) => [e.id, e.name]));
  const shown = all ? d.watch : d.watch.slice(0, WATCH_SHOWN);
  return (
    <SectionCard title={t('investments_overview.watch_title')}>
      <ul className="space-y-3 text-sm">
        {shown.map((w) => (
          <li key={`${w.code}/${w.envelopeId}/${w.holdingId ?? ''}`}>
            <Link to="/investments/$id" params={{ id: w.envelopeId }} search={w.holdingId || w.code === 'uc_share_below_floor' ? { tab: 'holdings' } : {}}
              className="font-medium hover:underline">{names.get(w.envelopeId) ?? ''}</Link>
            <span className="block text-muted-foreground">{watchText(t, w)}</span>
          </li>
        ))}
      </ul>
      {d.watch.length > WATCH_SHOWN ? (
        <Button type="button" variant="link" className="h-auto p-0" onClick={() => setAll((v) => !v)}>
          {all ? t('investments_overview.watch_less') : t('investments_overview.watch_all', { count: d.watch.length })}
        </Button>
      ) : null}
    </SectionCard>
  );
}

function watchText(t: TFunction, w: Watch): string {
  const date = w.on ? formatLocalDate(w.on, 'short') : '';
  const code = WATCH_CODES.find((c) => c === w.code);
  return code ? t(`investments_overview.watch.${code}`, { date }) : t('investments_overview.watch.other');
}

/** An envelope's amount in its own currency; in another currency than the household's, the converted one in a tooltip. */
function Amount({ value, converted }: { value: MoneyValue; converted?: MoneyValue | null }) {
  const { t } = useTranslation();
  if (!converted || converted.currency === value.currency) return <Money value={value} tabular />;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span tabIndex={0} className="tabular-nums underline decoration-dotted underline-offset-4">{formatMoney(value)}</span>
      </TooltipTrigger>
      <TooltipContent>{t('investments_overview.converted', { amount: formatMoney(converted) })}</TooltipContent>
    </Tooltip>
  );
}

function Envelopes({ d }: { d: InvestmentsOverview }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  const dash = <span aria-hidden="true">{t('investments.dash')}</span>;
  const name = (r: EnvelopeOverviewRow) => (
    <span className="inline-flex items-start gap-2">
      <span aria-hidden="true" className="mt-1.5 size-2.5 shrink-0 rounded-sm" style={{ background: kindColor(r.kind) }} />
      <span className="inline-flex flex-col gap-0.5">
        <span className="font-medium">{r.name}</span>
        <span className="text-sm text-muted-foreground">{[kindText(t, r.kind), r.institution].filter(Boolean).join(' · ')}</span>
      </span>
    </span>
  );
  const updated = (r: EnvelopeOverviewRow) => {
    const { asOf, staleDays } = r.figures;
    if (!asOf) return <span className="text-muted-foreground">{t('investments.no_value')}</span>;
    const stale = staleDays !== null && staleDays !== undefined;
    return (
      <span>
        <span className="inline-flex items-center gap-1">
          {stale ? <Clock aria-hidden="true" className="size-4" /> : null}
          <LocalDate value={asOf} style="short" />
          {stale ? <span className="sr-only">{t('investments.stale', { count: staleDays })}</span> : null}
        </span>
        {r.estimated && r.estimatedOn ? <Estimated on={r.estimatedOn} /> : null}
      </span>
    );
  };
  return (
    <SectionCard title={t('investments_overview.envelopes_title')} actions={
      <Link to="/investments" search={(prev: RootSearch & InvestmentsSearch) => ({ ...prev, tab: 'list' })} className="text-sm hover:underline">
        {t('investments_overview.see_list')}
      </Link>
    }>
      <p className="text-sm text-muted-foreground">{t('investments.before_tax')}</p>
      <SortableTable<EnvelopeOverviewRow> caption={t('investments_overview.envelopes_list')} rows={d.envelopes} rowKey={(r) => r.id}
        rowLink={(r) => ({ to: '/investments/$id', params: { id: r.id } })}
        columns={[
          { id: 'name', header: t('investments_overview.col_envelope'), sort: (r) => r.name, cell: name },
          { id: 'owner', header: t('investments_overview.col_owners'), cell: (r) => <OwnerChips owners={r.owners} /> },
          { id: 'value', header: t('lists.col.value'), align: 'end', sort: (r) => amountKey(r.valueConverted?.amount ?? null),
            cell: (r) => (r.figures.value ? <Amount value={r.figures.value} converted={r.valueConverted} /> : <span className="text-muted-foreground">{t('investments.no_value')}</span>) },
          { id: 'gain', header: t('investments_overview.col_gain'), align: 'end', sort: (r) => amountKey(r.gainConverted?.amount ?? null),
            cell: (r) => (r.figures.value ? <Gain figures={r.figures} /> : dash) },
          { id: 'annual', header: t('investments_overview.col_annual'), align: 'end', sort: (r) => r.figures.annualReturnPct ?? Number.NEGATIVE_INFINITY,
            cell: (r) => (r.figures.annualReturnPct === null || r.figures.annualReturnPct === undefined ? dash : <AnnualReturn value={r.figures.annualReturnPct} />) },
          { id: 'updated', header: t('investments_overview.col_updated'), sort: (r) => r.figures.asOf ?? '', cell: updated },
        ]}
        mobile={(r) => (
          <div className="space-y-1">
            <div className="flex items-center justify-between gap-2">{name(r)}<OwnerChips owners={r.owners} /></div>
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
              {r.figures.value ? <Amount value={r.figures.value} converted={r.valueConverted} /> : <span className="text-muted-foreground">{t('investments.no_value')}</span>}
              {r.figures.value ? <Gain figures={r.figures} /> : null}
            </div>
            {r.estimated && r.estimatedOn ? <Estimated on={r.estimatedOn} /> : null}
          </div>
        )} />
      <p className="flex flex-wrap justify-between gap-2 border-t pt-2 text-sm font-medium">
        <span>{t('investments_overview.total_row', { count: d.openCount })}</span>
        <span className="tabular-nums">{figure(d.value)}{d.estimatedCount > 0 ? <span className="font-normal text-muted-foreground"> {t('investments_overview.estimated')}</span> : null}{d.gain ? ` · ${signed(d.gain)}` : null}</span>
      </p>
    </SectionCard>
  );
}
