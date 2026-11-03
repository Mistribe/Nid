import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { Info } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { Field, type FieldControl } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { FormDialog } from '@/components/FormDialog';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { FEE_REFERENCES, HOUSEHOLD_HOLDINGS, type FeeReferences, type FeesCost, type HoldingOverviewRow, type HouseholdHoldings } from '../api';
import { toPlot } from '../chart/plot';
import { pct } from '../Figures';
import { KINDS } from '../format';
import { HOLDING_KINDS, holdingKindText } from '../holdings/holdingFormat';
import { holdingKindColor } from './colors';
import { ExposureSection } from './ExposureSection';
import '@/locales/investments';

type Scope = 'household' | 'me';

/** A share the server rounded, written without a sign. */
const share = (value: number) => new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(value);

/** A fee rate as the server rounded it, to 0.01 %. */
const rate = (value: number) => new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 2 }).format(value);

/** Investments dashboard §5.3: the holdings of every envelope together, from the server's figures; nothing computed here. */
export function HoldingsTab({ scope }: { scope: Scope }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', HOUSEHOLD_HOLDINGS, { params: { query: { scope } } });
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const d = answer.data;
  if (!d) return <Skeleton role="status" aria-busy="true" aria-label={t('investments_holdings.loading')} className="h-64 w-full" />;
  return (
    <div className="flex flex-col gap-6">
      <Figures d={d} />
      {d.concentrations.slice(0, 2).map((c) => (
        <p key={c.isin} role="note" className="flex items-start gap-2 rounded-lg bg-accent px-3 py-2 text-sm text-accent-foreground">
          <Info aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
          {t('investments_holdings.concentration', { name: c.name, pct: c.sharePct === null || c.sharePct === undefined ? '' : t('investments.pct', { value: share(c.sharePct) }), count: c.envelopeIds.length })}
        </p>
      ))}
      <Kinds d={d} />
      <ExposureSection d={d} />
      <Rows d={d} />
      <div className="grid gap-4 md:grid-cols-2">
        <Fees d={d} />
        <WatchCard d={d} />
      </div>
      <EnvelopeFees d={d} />
      <Projection d={d} />
    </div>
  );
}

function useFigure(d: HouseholdHoldings) {
  const { t } = useTranslation();
  const codes = [...new Set(d.findings.filter((f) => f.code === 'core.fx_rate_missing').map((f) => (f.reference ?? '').split(':')[0]))].join(', ');
  return (value: MoneyValue | null | undefined): ReactNode =>
    value ? formatMoney(value)
      : <span>{t('investments_holdings.unknown')}{codes ? <Sub>{t('investments_holdings.unknown_rate', { codes })}</Sub> : null}</span>;
}

function Sub({ children }: { children: ReactNode }) {
  return <span className="block text-sm font-normal text-muted-foreground">{children}</span>;
}

function Figures({ d }: { d: HouseholdHoldings }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  const percent = (v?: number | null) => (v === null || v === undefined ? '' : t('investments.pct', { value: share(v) }));
  const floors = d.envelopesUc.filter((e) => e.floorPct !== null && e.floorPct !== undefined);
  const next = [...d.holdings].filter((h) => h.maturesOn).sort((a, b) => a.maturesOn!.localeCompare(b.maturesOn!))[0];
  return (
    <StatLine items={[
      { label: t('investments_holdings.detailed'),
        value: <>{figure(d.detailed)}{d.estimated ? <span className="text-sm font-normal text-muted-foreground"> {t('investments_holdings.estimated')}</span> : null}<Sub>{t('investments_holdings.detailed_note', { count: d.detailedCount, total: d.envelopeCount, pct: percent(d.detailedPct) })}</Sub></> },
      ...(d.ucSharePct === null || d.ucSharePct === undefined ? [] : [{ label: t('investments_holdings.uc'), value: (
        <>{percent(d.ucSharePct)}<Sub>{floors.length === 1 ? t('investments_holdings.uc_note', { floor: percent(floors[0]!.floorPct) }) : t('investments_holdings.uc_note_plain')}</Sub></>
      ) }]),
      { label: t('investments_holdings.cost', { year: d.costYear }), value: (
        <>{d.cost ? `≈ ${formatMoney(d.cost)}` : '—'}
          <Sub>{d.costPct === null || d.costPct === undefined ? t('investments_holdings.cost_note_plain') : t('investments_holdings.cost_note', { pct: percent(d.costPct) })}</Sub></>
      ) },
      ...(next ? [{ label: t('investments_holdings.maturity'), value: <>{formatLocalDate(next.maturesOn!, 'short')}<Sub>{next.name}</Sub></> }] : []),
    ]} />
  );
}

/** dataviz.md §2: one stacked bar of the kinds of holding (their fixed colour), then the table the person reads; what is not detailed under it. */
function Kinds({ d }: { d: HouseholdHoldings }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  const outside = [
    ...d.undetailedEnvelopes.map((e) => `${e.name} ${e.value ? formatMoney(e.value) : t('investments_holdings.unknown')}`),
    ...(d.unallocated && !/^-?0*(\.0*)?$/.test(d.unallocated.amount) ? [t('investments_holdings.unallocated', { amount: formatMoney(d.unallocated) })] : []),
  ];
  if (d.byKind.length === 0 && outside.length === 0) return null;
  return (
    <SectionCard title={t('investments_holdings.kinds_title')}>
      {d.byKind.length > 0 ? (
        <>
          <div aria-hidden="true" className="flex h-3 gap-0.5 overflow-hidden rounded-full bg-muted">
            {d.byKind.map((k) => <span key={k.kind} style={{ width: `${k.sharePct ?? 0}%`, background: holdingKindColor(k.kind) }} />)}
          </div>
          <Table aria-label={t('investments_holdings.kinds_table')}>
            <TableBody>
              {d.byKind.map((k) => (
                <TableRow key={k.kind}>
                  <TableHead scope="row" className="h-auto py-1.5 font-normal text-foreground">
                    <span className="inline-flex items-center gap-2">
                      <span aria-hidden="true" className="size-2.5 rounded-sm" style={{ background: holdingKindColor(k.kind) }} />{holdingKindText(t, k.kind)}
                    </span>
                  </TableHead>
                  <TableCell className="py-1.5 text-right tabular-nums">{figure(k.value)}</TableCell>
                  <TableCell className="py-1.5 text-right tabular-nums text-muted-foreground">{k.sharePct === null || k.sharePct === undefined ? '' : t('investments.pct', { value: share(k.sharePct) })}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </>
      ) : null}
      {outside.length > 0 ? <p className="text-sm text-muted-foreground">{t('investments_holdings.outside', { list: outside.join(', ') })}</p> : null}
    </SectionCard>
  );
}

const pctOrDash = (t: TFunction, v?: number | null, perYear = false) =>
  v === null || v === undefined ? <span aria-hidden="true">{t('investments.dash')}</span>
    : <span className="tabular-nums">{t(perYear ? 'investments.pct_per_year' : 'investments.pct', { value: pct(v) })}</span>;

/** Spec §5.3: the holdings by value, or grouped by envelope (the server's order within each); a row opens the holding. */
function Rows({ d }: { d: HouseholdHoldings }) {
  const { t } = useTranslation();
  const [group, setGroup] = useState<'holding' | 'envelope'>('holding');
  if (d.holdings.length === 0) return null;
  const rows = group === 'holding' ? d.holdings
    : [...d.holdings].sort((a, b) => a.envelopeName.localeCompare(b.envelopeName, 'fr') || d.holdings.indexOf(a) - d.holdings.indexOf(b));
  const name = (r: HoldingOverviewRow) => (
    <span className="inline-flex items-start gap-2">
      <span aria-hidden="true" className="mt-1.5 size-2.5 shrink-0 rounded-sm" style={{ background: holdingKindColor(r.kind) }} />
      <span className="inline-flex flex-col gap-0.5">
        <span className="font-medium">{r.name}</span>
        <span className="text-sm text-muted-foreground">{holdingKindText(t, r.kind)}</span>
      </span>
    </span>
  );
  const weight = (r: HoldingOverviewRow) => (
    <span className="inline-flex items-center gap-2">
      <span aria-hidden="true" className="h-1.5 w-14 rounded-full bg-muted">
        <span className="block h-full rounded-full bg-muted-foreground" style={{ width: `${Math.min(100, (r.sharePct ?? 0) / Math.max(1, d.holdings[0]?.sharePct ?? 1) * 100)}%` }} />
      </span>
      <span className="text-sm tabular-nums">{r.sharePct === null || r.sharePct === undefined ? '' : t('investments.pct', { value: share(r.sharePct) })}</span>
    </span>
  );
  return (
    <SectionCard title={t('investments_holdings.rows_title')} actions={
      <Segmented value={group} aria-label={t('investments_holdings.group')} onValueChange={(v) => setGroup(v === 'envelope' ? 'envelope' : 'holding')}
        options={[{ value: 'holding', label: t('investments_holdings.group_holding') }, { value: 'envelope', label: t('investments_holdings.group_envelope') }]} />
    }>
      <SortableTable<HoldingOverviewRow> caption={t('investments_holdings.rows_list')} rows={rows} rowKey={(r) => `${r.envelopeId}/${r.holdingId}`}
        rowLink={(r) => ({ to: '/investments/$id/holdings/$holdingId', params: { id: r.envelopeId, holdingId: r.holdingId } })}
        columns={[
          { id: 'name', header: t('investments_holdings.col_holding'), sort: (r) => r.name, cell: name },
          { id: 'envelope', header: t('investments_holdings.col_envelope'), sort: (r) => r.envelopeName, cell: (r) => r.envelopeName },
          { id: 'value', header: t('investments_holdings.col_value'), align: 'end', sort: (r) => amountKey(r.valueConverted?.amount ?? null),
            cell: (r) => <span className="tabular-nums">{formatMoney({ amount: r.value, currency: r.currency })}</span> },
          { id: 'weight', header: t('investments_holdings.col_weight'), sort: (r) => r.sharePct ?? -1, cell: weight },
          { id: 'gain', header: t('investments_holdings.col_gain'), align: 'end', sort: (r) => r.gainPct ?? Number.NEGATIVE_INFINITY, cell: (r) => pctOrDash(t, r.gainPct) },
          { id: 'annual', header: t('investments_holdings.col_annual'), align: 'end', sort: (r) => r.annualReturnPct ?? Number.NEGATIVE_INFINITY, cell: (r) => pctOrDash(t, r.annualReturnPct, true) },
          { id: 'fees', header: t('investments_holdings.col_fees'), align: 'end', sort: (r) => r.totalFeesPct ?? -1,
            cell: (r) => (r.totalFeesPct === null || r.totalFeesPct === undefined ? <span aria-hidden="true">{t('investments.dash')}</span>
              : <span className="tabular-nums">{t('investments.pct', { value: share(r.totalFeesPct) })}</span>) },
        ]}
        mobile={(r) => (
          <div className="space-y-1">
            <div className="flex items-center justify-between gap-2">{name(r)}<span className="text-sm text-muted-foreground">{r.envelopeName}</span></div>
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
              <span className="tabular-nums">{formatMoney({ amount: r.value, currency: r.currency })}</span>{weight(r)}
            </div>
          </div>
        )} />
    </SectionCard>
  );
}

/** D18: the estimated cost of each holding with a report, largest first, always said to be an estimate. */
function Fees({ d }: { d: HouseholdHoldings }) {
  const { t } = useTranslation();
  const costed = d.holdings.filter((h) => h.implicitCost).sort((a, b) => amountKey(b.implicitCost!.amount) - amountKey(a.implicitCost!.amount));
  const max = costed[0] ? Math.max(1, toPlot(costed[0].implicitCost!.amount)) : 1;
  // D18: cash has no fees to report.
  const missing = d.holdings.filter((h) => (h.totalFeesPct === null || h.totalFeesPct === undefined) && h.kind !== 'cash');
  // One name held in several envelopes is said once, with how many times.
  const without = [...new Set(missing.map((h) => h.name))].map((name) => {
    const count = missing.filter((h) => h.name === name).length;
    return count > 1 ? t('investments_holdings.fees_without_name', { name, count }) : name;
  });
  const reported = d.holdings.some((h) => h.totalFeesPct !== null && h.totalFeesPct !== undefined);
  return (
    <SectionCard title={t('investments_holdings.fees_title', { year: d.costYear })}>
      {costed.length === 0 ? <p className="text-sm text-muted-foreground">{t(reported ? 'investments_holdings.fees_no_estimate' : 'investments_holdings.fees_none', { year: d.costYear, before: d.costYear - 1 })}</p> : (
        <ul className="space-y-2 text-sm">
          {costed.map((h) => (
            <li key={`${h.envelopeId}/${h.holdingId}`} className="grid grid-cols-[minmax(0,11rem)_1fr_auto] items-center gap-3">
              <span className="truncate">{h.name} <span className="text-muted-foreground">{t('investments.pct', { value: share(h.totalFeesPct!) })}</span>
                {h.costSharePct != null ? <span className="block text-xs text-muted-foreground">{t('investments_holdings.fees_share', { pct: t('investments.pct', { value: share(h.costSharePct) }) })}</span> : null}
              </span>
              <span aria-hidden="true" className="h-2.5 rounded-full bg-muted">
                <span className="block h-full rounded-full bg-[var(--chart-2)]" style={{ width: `${(toPlot(h.implicitCost!.amount) / max) * 100}%` }} />
              </span>
              <span className="text-right font-medium tabular-nums">≈ {formatMoney(h.implicitCost!)}</span>
            </li>
          ))}
        </ul>
      )}
      <p className="text-sm text-muted-foreground">{t('investments_holdings.fees_note')}</p>
      {d.withoutReportCount > 0 ? <p className="text-sm text-muted-foreground">{t('investments_holdings.fees_without', { count: d.withoutReportCount, year: d.costYear })}</p> : null}
      {without.length > 0 ? <p className="text-sm text-muted-foreground">{t('investments_holdings.fees_without_list', { year: d.costYear, list: without.join(', ') })}</p> : null}
    </SectionCard>
  );
}

const rateOrDash = (t: TFunction, v?: number | null) =>
  v === null || v === undefined ? <span aria-hidden="true">{t('investments.dash')}</span> : <span className="tabular-nums">{t('investments.pct', { value: rate(v) })}</span>;

/** Issue #103: each detailed envelope's fees, weighted by the server; the dearest first. */
function EnvelopeFees({ d }: { d: HouseholdHoldings }) {
  const { t } = useTranslation();
  if (d.envelopeFees.length === 0) return null;
  return (
    <SectionCard title={t('investments_holdings.envelope_fees_title', { year: d.costYear })}>
      {/* A phone reads a list: six columns would scroll the fees out of sight. */}
      <ul className="space-y-2 text-sm md:hidden">
        {d.envelopeFees.map((e) => (
          <li key={e.envelopeId}>
            <Link to="/investments/$id" params={{ id: e.envelopeId }} search={{ tab: 'holdings' }} className="font-medium hover:underline">{e.name}</Link>
            <span className="block text-muted-foreground">
              {e.totalFeesPct != null
                ? t('investments_holdings.envelope_fees_line', {
                  pct: t('investments.pct', { value: rate(e.totalFeesPct) }), cost: e.yearlyCost ? formatMoney(e.yearlyCost) : t('investments.dash'),
                  covered: e.coveredPct != null ? t('investments.pct', { value: share(e.coveredPct) }) : t('investments.dash') })
                : t('investments_holdings.envelope_fees_none')}
            </span>
          </li>
        ))}
      </ul>
      <Table aria-label={t('investments_holdings.envelope_fees_table')} className="hidden md:table">
        <TableHeader>
          <TableRow>
            <TableHead>{t('investments_holdings.col_envelope')}</TableHead>
            <TableHead className="text-right">{t('investments_holdings.col_contract_fees')}</TableHead>
            <TableHead className="text-right">{t('investments_holdings.col_support_fees')}</TableHead>
            <TableHead className="text-right">{t('investments_holdings.col_total_fees')}</TableHead>
            <TableHead className="text-right">{t('investments_holdings.col_yearly_cost')}</TableHead>
            <TableHead className="text-right">{t('investments_holdings.col_covered')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {d.envelopeFees.map((e) => (
            <TableRow key={e.envelopeId}>
              <TableHead scope="row" className="font-normal">
                <Link to="/investments/$id" params={{ id: e.envelopeId }} search={{ tab: 'holdings' }} className="hover:underline">{e.name}</Link>
              </TableHead>
              <TableCell className="text-right">{rateOrDash(t, e.contractFeesPct)}</TableCell>
              <TableCell className="text-right">{rateOrDash(t, e.supportFeesPct)}</TableCell>
              <TableCell className="text-right">{rateOrDash(t, e.totalFeesPct)}</TableCell>
              <TableCell className="text-right tabular-nums">{e.yearlyCost ? `≈ ${formatMoney(e.yearlyCost)}` : t('investments.dash')}</TableCell>
              <TableCell className="text-right tabular-nums">{e.coveredPct != null ? t('investments.pct', { value: share(e.coveredPct) }) : t('investments.dash')}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <p className="text-sm text-muted-foreground">{t('investments_holdings.envelope_fees_note')}</p>
    </SectionCard>
  );
}

/** Issue #209: what the fees take over 10 and 20 years against the household's references, by envelope then holding; all server figures. */
function Projection({ d }: { d: HouseholdHoldings }) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState(false);
  const p = d.projection;
  const money = (v?: MoneyValue | null) => (v ? formatMoney(v) : t('investments.dash'));
  const percentText = (v: number) => t('investments.pct', { value: rate(v) });
  const actions = <Button type="button" variant="outline" size="sm" onClick={() => setEditing(true)}>{t('investments_holdings.references_edit')}</Button>;
  const row = (c: FeesCost, key: string, total: boolean) => (
    <TableRow key={key}>
      <TableHead scope="row" className={total ? 'whitespace-normal' : 'whitespace-normal pl-6 font-normal'}>
        {c.name}
        {/* On a phone the rates move under the name, so three columns fit. */}
        <span className="block text-xs font-normal text-muted-foreground md:hidden">
          {t('investments_holdings.fees_vs_reference', { fees: percentText(c.feesPct), reference: percentText(c.referencePct) })}
        </span>
      </TableHead>
      <TableCell className="hidden text-right tabular-nums md:table-cell">{percentText(c.feesPct)}</TableCell>
      <TableCell className="hidden text-right tabular-nums md:table-cell">{percentText(c.referencePct)}</TableCell>
      <TableCell className="hidden text-right tabular-nums md:table-cell">{money(c.yearly)}</TableCell>
      <TableCell className="hidden text-right tabular-nums md:table-cell">{money(c.cost10)}</TableCell>
      <TableCell className="text-right tabular-nums">{money(c.cost20)}</TableCell>
      <TableCell className={total ? 'text-right font-medium tabular-nums' : 'text-right tabular-nums'}>{money(c.gap20)}</TableCell>
    </TableRow>
  );
  return (
    <SectionCard title={t('investments_holdings.projection_title')} actions={actions}>
      {p ? (
        <>
          <StatLine items={[
            { label: t('investments_holdings.projection_yearly'), value: money(p.yearly) },
            { label: t('investments_holdings.projection_cost', { count: 10 }), value: money(p.cost10) },
            { label: t('investments_holdings.projection_cost', { count: 20 }), value: money(p.cost20) },
            { label: t('investments_holdings.projection_above'), value: money(p.aboveReference20) },
          ]} />
          <Table aria-label={t('investments_holdings.projection_table')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('investments_holdings.col_envelope_holding')}</TableHead>
                <TableHead className="hidden text-right md:table-cell">{t('investments_holdings.col_total_fees')}</TableHead>
                <TableHead className="hidden text-right md:table-cell">{t('investments_holdings.col_reference')}</TableHead>
                <TableHead className="hidden text-right md:table-cell">{t('investments_holdings.col_yearly_cost')}</TableHead>
                <TableHead className="hidden text-right md:table-cell">{t('investments_holdings.col_cost', { count: 10 })}</TableHead>
                <TableHead className="text-right">{t('investments_holdings.col_cost', { count: 20 })}</TableHead>
                <TableHead className="text-right">{t('investments_holdings.col_gap')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {p.envelopes.flatMap((e) => [
                row(e.total, e.envelopeId, true),
                ...e.holdings.map((h) => row(h, `${e.envelopeId}-${h.holdingId}`, false)),
              ])}
            </TableBody>
          </Table>
        </>
      ) : <p className="text-sm text-muted-foreground">{t('investments_holdings.projection_none', { year: d.costYear })}</p>}
      <p className="text-sm text-muted-foreground">{t('investments_holdings.projection_note', { pct: percentText(d.references.assumedReturnPct) })}</p>
      {editing ? <ReferencesDialog references={d.references} onClose={() => setEditing(false)} onDone={(message) => { setEditing(false); toast.success(message); }} /> : null}
    </SectionCard>
  );
}

/** Issue #209: the household's references and assumed return, every kind with its current value (a default until changed). */
function ReferencesDialog({ references, onClose, onDone }: { references: FeeReferences; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [error, setError] = useState('');
  const save = $api.useMutation('put', FEE_REFERENCES, {
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['get', HOUSEHOLD_HOLDINGS] }),
    onError: (e) => setProblem(present(toProblem(e))),
  });
  const envelopeKinds = KINDS.filter((k) => k in references.envelopes);
  const holdingKinds = HOLDING_KINDS.filter((k) => k in references.holdings);
  const input = (name: string, value: number) => (control: FieldControl) =>
    <Input {...control} name={name} inputMode="decimal" className="w-24" defaultValue={rate(value)} />;
  return (
    <FormDialog title={t('investments_holdings.references_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const fields = new FormData(event.currentTarget);
          // Rates in %, not amounts: a French keyboard types a comma.
          const read = (name: string) => Number(((fields.get(name) as string | null) ?? '').trim().replace(',', '.') || Number.NaN);
          const assumedReturnPct = read('return');
          const envelopes = Object.fromEntries(envelopeKinds.map((k) => [k, read(`envelope-${k}`)]));
          const holdings = Object.fromEntries(holdingKinds.map((k) => [k, read(`holding-${k}`)]));
          const fees = [...Object.values(envelopes), ...Object.values(holdings)];
          if (Number.isNaN(assumedReturnPct) || assumedReturnPct < -10 || assumedReturnPct > 20 || fees.some((v) => Number.isNaN(v) || v < 0 || v > 10)) {
            setError(t('investments_holdings.references_invalid'));
            return;
          }
          setError('');
          save.mutate({ body: { assumedReturnPct, envelopes, holdings } }, { onSuccess: () => done(t('investments_holdings.references_saved')) });
        }}>
          {problem ? <ProblemMessage problem={problem} /> : null}
          <p className="text-sm text-muted-foreground">{t('investments_holdings.references_hint')}</p>
          {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
          <Field label={t('investments_holdings.projection_return')}>{input('return', references.assumedReturnPct)}</Field>
          <fieldset className="space-y-2">
            <legend className="text-sm font-medium">{t('investments_holdings.references_contract')}</legend>
            <div className="grid grid-cols-2 gap-3">
              {envelopeKinds.map((k) => <Field key={k} label={t(`investments.kind.${k}`)}>{input(`envelope-${k}`, references.envelopes[k]!)}</Field>)}
            </div>
          </fieldset>
          <fieldset className="space-y-2">
            <legend className="text-sm font-medium">{t('investments_holdings.references_support')}</legend>
            <div className="grid grid-cols-2 gap-3">
              {holdingKinds.map((k) => <Field key={k} label={holdingKindText(t, k)}>{input(`holding-${k}`, references.holdings[k]!)}</Field>)}
            </div>
          </fieldset>
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('investments_holdings.references_save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

const WATCH = ['holding_maturity', 'unexplained_units', 'uc_share_below_floor', 'holding_value_mismatch'] as const;

function WatchCard({ d }: { d: HouseholdHoldings }) {
  const { t } = useTranslation();
  if (d.watch.length === 0) return null;
  const holding = (id?: string | null) => d.holdings.find((h) => h.holdingId === id)?.name ?? '';
  const envelope = (id: string) => d.holdings.find((h) => h.envelopeId === id)?.envelopeName ?? '';
  return (
    <SectionCard title={t('investments_holdings.watch_title')}>
      <ul className="space-y-3 text-sm">
        {d.watch.map((w) => {
          const code = WATCH.find((c) => c === w.code);
          const text = code ? t(`investments_holdings.watch.${code}`, { holding: holding(w.holdingId), date: w.on ? formatLocalDate(w.on, 'short') : '' })
            : t('investments_holdings.watch.other');
          return (
            <li key={`${w.code}/${w.envelopeId}/${w.holdingId ?? ''}`}>
              {w.holdingId
                ? <Link to="/investments/$id/holdings/$holdingId" params={{ id: w.envelopeId, holdingId: w.holdingId }} className="font-medium hover:underline">{envelope(w.envelopeId)}</Link>
                : <Link to="/investments/$id" params={{ id: w.envelopeId }} search={{ tab: 'holdings' }} className="font-medium hover:underline">{envelope(w.envelopeId)}</Link>}
              <span className="block text-muted-foreground">{text}</span>
            </li>
          );
        })}
      </ul>
    </SectionCard>
  );
}
