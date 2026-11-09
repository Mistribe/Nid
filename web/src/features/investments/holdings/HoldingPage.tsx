import { useParams } from '@tanstack/react-router';
import { AlertTriangle, Plus } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { PageTrail, type Crumb } from '@/components/page/trail';
import { RowMenu } from '@/components/page/RowMenu';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { SeriesLink } from '@/components/page/SeriesLink';
import { COST_REPORTS, ENVELOPE, HOLDING_BREAKDOWN, HOLDING_SERIES, HOLDINGS, MOVEMENTS, type CostReport, type Holding } from '@/api/investments';
import { SeriesChart } from '../chart/ValueChart';
import { AnnualReturn, pct } from '../Figures';
import { Direction } from '@/components/Direction';
import { realReturnItem } from '../realReturn';
import { BreakdownDialog } from './BreakdownDialog';
import { SourceLink } from '@/features/documents/SourceLink';
import { hasBreakdown, REGIONS, regionText, SECTORS, sectorText, sharePct } from './breakdown';
import { CostReportDialog } from './CostReportDialog';
import { HoldingDialog } from './HoldingDialog';
import { formatUnits, hasUnits, holdingKindText, movementKindText, percent } from './holdingFormat';
import { MovementDialog } from './MovementDialog';
import { PriceCell } from './PriceCell';
import '@/locales/investments';
import '@/locales/forms';
import { PageSkeleton } from '@/components/page/skeletons';

const TABS = ['overview', 'movements', 'costs'] as const;
type Dialog = { kind: 'movement' } | { kind: 'edit' } | { kind: 'cost'; editing?: CostReport } | { kind: 'breakdown' };

/**
 * Issue #34 §7: one holding of an envelope, on the page grammar — its figures inline, then its curve (value against
 * money in − out), its movements and its « Coûts » (the yearly cost reports and the estimated implicit cost).
 */
export function HoldingPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id, holdingId } = useParams({ from: '/investments/$id/holdings/$holdingId' });
  const path = { params: { path: { id, holdingId } } };
  const envelope = $api.useQuery('get', ENVELOPE, { params: { path: { id } } });
  const holdings = $api.useQuery('get', HOLDINGS, { params: { path: { id } } });
  const movements = $api.useQuery('get', MOVEMENTS, path);
  const reports = $api.useQuery('get', COST_REPORTS, path);
  const series = $api.useQuery('get', HOLDING_SERIES, path);
  const listed = holdings.data?.holdings.find((h) => h.id === holdingId);
  const marketHolding = listed?.kind;
  const hasIsin = Boolean(listed?.isin);
  // 404: no breakdown yet. A euro fund, a cash or a holding without an ISIN (a breakdown is keyed by it) has none: nothing is asked.
  const breakdown = $api.useQuery('get', HOLDING_BREAKDOWN, path, { enabled: marketHolding !== undefined && hasBreakdown(marketHolding) && hasIsin, retry: false });
  const [tab, setTab] = useDetailTab(TABS);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const open = (next: Dialog) => setDialog(next);
  const done = (message: string) => { setDialog(null); toast.success(message); };

  // The envelope's crumb as soon as it is read: a refusal or a missing holding still leads back to it.
  const crumbs: Crumb[] = [{ label: t('nav.investments'), to: '/investments' },
    ...(envelope.data ? [{ label: envelope.data.displayName, to: '/investments/$id', params: { id } } as const] : [])];
  const trail = <PageTrail crumbs={crumbs} />;
  const error = envelope.error ?? holdings.error;
  if (error) return <>{trail}<ProblemMessage problem={present(toProblem(error))} /></>;
  if (!envelope.data || !holdings.data) return <>{trail}<PageSkeleton label={t('holdings.loading')} actions={2} stats={4} tabs={3} /></>;
  const holding = holdings.data.holdings.find((h) => h.id === holdingId);
  if (!holding) return <>{trail}<ProblemMessage problem={{ kind: 'not_found' }} /></>;

  const detail = envelope.data;
  const figures = holding.figures;
  const writable = detail.canWrite && !detail.closed;
  const absent = <span className="text-base font-normal text-muted-foreground">{t('investments.no_value')}</span>;
  const items = movements.data?.items ?? [];
  const pricesOn = holdings.data.estimate?.missingReason !== 'disabled';

  const actions = writable && !holding.closedOn ? (
    <>
      <Button type="button" onClick={() => open({ kind: 'movement' })}><Plus aria-hidden="true" />{t('holdings.movement.open')}</Button>
      <RowMenu label={t('page.more_actions')} items={[
        { label: t('holdings.edit'), onSelect: () => open({ kind: 'edit' }) },
        { label: t('holdings.costs.add'), onSelect: () => open({ kind: 'cost' }) },
      ]} />
    </>
  ) : null;

  return (
    <article className="space-y-6">
      <PageHeader title={holding.name} actions={actions} crumbs={crumbs}
        meta={<Meta holding={holding} />}>
        <section aria-label={t('holdings.figures')} className="space-y-2">
          <StatLine items={[
            { label: figures ? t('envelope.value_at', { date: formatLocalDate(figures.asOf, 'short') }) : t('envelope.value'), value: figures ? <Money value={figures.value} tabular /> : absent },
            { label: t('holdings.money_in'), value: figures ? <Money value={figures.moneyIn} tabular /> : absent },
            { label: t('holdings.money_out'), value: figures ? <Money value={figures.moneyOut} tabular /> : absent },
            { label: t('envelope.gain'), value: figures ? (
              <Direction amount={figures.gain}>
                {figures.gainPct != null ? <span className="tabular-nums">{t('investments.pct', { value: pct(figures.gainPct) })}</span> : null}
              </Direction>
            ) : absent },
            ...(holding.price ? [{ label: t('holding_prices.price'), value: <PriceCell envelope={id} holding={holding} writable={writable} /> }] : []),
            ...(holding.isin && pricesOn ? [{ label: t('holding_prices.auto_price'), value: t(holding.autoPrice ? 'holding_prices.auto_price_on' : 'holding_prices.auto_price_off') }] : []),
            ...(figures?.annualReturnPct != null ? [{ label: t('envelope.annual_return'), value: <AnnualReturn value={figures.annualReturnPct} /> }] : []),
            ...realReturnItem(t, holding.realReturn),
            ...(figures ? [
              { label: t('holdings.explicit_fees'), value: <Money value={figures.explicitFees} tabular /> },
              { label: t('holdings.gain_before_fees'), value: <Money value={figures.gainBeforeFees} tabular /> },
            ] : []),
          ]} />
          <p className="text-sm text-muted-foreground">{t('holdings.figures_note')}</p>
        </section>
      </PageHeader>
      {dialog?.kind === 'movement' ? <MovementDialog envelope={detail} holding={holding} onClose={() => setDialog(null)} onDone={done} /> : null}
      {dialog?.kind === 'edit' ? <HoldingDialog envelope={detail} editing={holding} pricesOn={pricesOn} onClose={() => setDialog(null)} onDone={done} /> : null}
      {dialog?.kind === 'breakdown' ? <BreakdownDialog envelope={detail} holding={holding} current={breakdown.data} onClose={() => setDialog(null)} onDone={done} /> : null}
      {dialog?.kind === 'cost' ? <CostReportDialog envelope={detail} holding={holding} editing={dialog.editing} onClose={() => setDialog(null)} onDone={done} /> : null}

      <DetailTabs value={tab} onChange={setTab} tabs={[
        {
          id: 'overview', label: t('envelope.tab.overview'),
          content: (
            <>
              <Section title={t('envelope.evolution')}
                actions={hasUnits(holding.kind) ? <SeriesLink keys={[`holding:${detail.id}:${holding.id}:price`]} /> : undefined}>
                {series.error ? <ProblemMessage problem={present(toProblem(series.error))} />
                  : !series.data ? <div role="status" aria-busy="true" aria-label={t('envelope.chart_loading')} className="h-64 shimmer rounded-md border bg-muted" />
                    : <SeriesChart id={holding.id} points={series.data.points} currency={detail.currency} netLabel={t('holdings.net_money_in')}
                      title={t('holdings.chart_title')} tableLabel={t('holdings.chart_table')} />}
              </Section>
              {hasBreakdown(holding.kind) && holding.isin ? (
                <Section title={t('breakdown.title')}>
                  {breakdown.data ? (
                    <div className="space-y-3">
                      <p className="text-sm text-muted-foreground">
                        {t('breakdown.as_of', { date: formatLocalDate(breakdown.data.asOf) })}<SourceLink documentId={breakdown.data.documentId} />
                        {breakdown.data.stale ? <span className="ml-2 inline-flex items-center gap-1 text-foreground"><AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />{t('breakdown.old')}</span> : null}
                      </p>
                      <Parts label={t('breakdown.regions')} parts={breakdown.data.regions} codes={REGIONS} kind="region" />
                      {breakdown.data.sectors ? <Parts label={t('breakdown.sectors')} parts={breakdown.data.sectors} codes={SECTORS} kind="sector" /> : null}
                    </div>
                  ) : breakdown.error && toProblem(breakdown.error).status !== 404 ? <ProblemMessage problem={present(toProblem(breakdown.error))} />
                    : breakdown.isPending ? null : <p className="text-muted-foreground">{t('breakdown.none')}</p>}
                  {writable && !holding.closedOn ? (
                    <Button type="button" variant="outline" size="sm" disabled={breakdown.isPending} onClick={() => open({ kind: 'breakdown' })}>{t(breakdown.data ? 'breakdown.update' : 'breakdown.fill')}</Button>
                  ) : null}
                </Section>
              ) : null}
              {holding.note ? <Section title={t('holdings.form.note')}><p className="whitespace-pre-line">{holding.note}</p></Section> : null}
            </>
          ),
        },
        {
          id: 'movements', label: t('holdings.tab.movements', { count: items.length }),
          content: (
            <Section title={t('holdings.movements')} srTitle>
              {movements.error ? <ProblemMessage problem={present(toProblem(movements.error))} /> : (
                <Table aria-label={t('holdings.movements')}>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t('envelope.date')}</TableHead>
                      <TableHead>{t('holdings.movement.kind')}</TableHead>
                      <TableHead className="text-right">{t('envelope.amount')}</TableHead>
                      <TableHead className="text-right">{t('holdings.units')}</TableHead>
                      <TableHead className="text-right">{t('holdings.unit_price')}</TableHead>
                      <TableHead>{t('holdings.movement.label')}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {items.length === 0 ? (
                      <TableRow><TableCell colSpan={6} className="text-muted-foreground">{t('holdings.no_movements')}</TableCell></TableRow>
                    ) : [...items].reverse().map((m) => (
                      <TableRow key={m.id}>
                        <TableCell><LocalDate value={m.occurredOn} /></TableCell>
                        <TableCell className="whitespace-normal">
                          {movementKindText(t, m.kind)}
                          {m.switchLegs ? (
                            // Issue #45: the whole operation, money leaving first.
                            <div className="text-xs text-muted-foreground">
                              {m.switchLegs.filter((l) => l.kind !== 'switch_in').map((l) => l.holdingName).join(', ')}
                              {' → '}
                              {m.switchLegs.filter((l) => l.kind === 'switch_in').map((l) => l.holdingName).join(', ')}
                            </div>
                          ) : null}
                        </TableCell>
                        <TableCell className="text-right"><Money value={m.amount} tabular /></TableCell>
                        <TableCell className="text-right tabular-nums">{m.units ? formatUnits(m.units) : null}</TableCell>
                        <TableCell className="text-right tabular-nums">{m.unitPrice ? formatUnits(m.unitPrice) : null}</TableCell>
                        <TableCell className="whitespace-normal">{m.label}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </Section>
          ),
        },
        {
          id: 'costs', label: t('holdings.tab.costs'),
          content: (
            <Section title={t('holdings.costs.title')} srTitle>
              <p className="text-sm text-muted-foreground">{t('holdings.costs.intro')}</p>
              {reports.error ? <ProblemMessage problem={present(toProblem(reports.error))} /> : (
                <Table aria-label={t('holdings.costs.title')}>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t('holdings.costs.year')}</TableHead>
                      <TableHead className="text-right">{t('holdings.costs.grossReturnPct')}</TableHead>
                      <TableHead className="text-right">{t('holdings.costs.totalFeesPct')}</TableHead>
                      <TableHead className="text-right">{t('holdings.costs.netReturnPct')}</TableHead>
                      <TableHead className="text-right">{t('holdings.costs.implicit')}</TableHead>
                      {writable ? <TableHead className="w-0"><span className="sr-only">{t('envelope.actions')}</span></TableHead> : null}
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {(reports.data?.items ?? []).length === 0 ? (
                      <TableRow><TableCell colSpan={writable ? 6 : 5} className="text-muted-foreground">{t('holdings.costs.none')}</TableCell></TableRow>
                    ) : reports.data!.items.map((r) => (
                      <TableRow key={r.year}>
                        <TableCell>
                          {r.year}
                          {r.warnings.length > 0 ? (
                            <span className="ml-2 inline-flex items-center gap-1 text-sm">
                              <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />{t('holdings.warning.cost_report_inconsistent_short')}
                            </span>
                          ) : null}
                        </TableCell>
                        <TableCell className="text-right tabular-nums">{t('investments.pct', { value: percent(r.grossReturnPct) })}</TableCell>
                        <TableCell className="text-right tabular-nums">{t('investments.pct', { value: percent(r.totalFeesPct) })}</TableCell>
                        <TableCell className="text-right tabular-nums">{t('investments.pct', { value: percent(r.netReturnPct) })}</TableCell>
                        <TableCell className="text-right">
                          {r.estimatedImplicitCost ? <Money value={r.estimatedImplicitCost} tabular /> : <span className="text-muted-foreground">{t('investments.dash')}</span>}
                        </TableCell>
                        {writable ? (
                          <TableCell>
                            <RowMenu label={t('holdings.costs.row_menu', { year: r.year })} items={[{ label: t('envelope.correct'), onSelect: () => open({ kind: 'cost', editing: r }) }]} />
                          </TableCell>
                        ) : null}
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
              <p className="text-sm text-muted-foreground">{t('holdings.costs.implicit_note')}</p>
            </Section>
          ),
        },
      ]} />
    </article>
  );
}

function Meta({ holding }: { holding: Holding }) {
  const { t } = useTranslation();
  const parts = [
    holdingKindText(t, holding.kind),
    holding.isin,
    holding.maturesOn ? t('holdings.matures_on', { date: formatLocalDate(holding.maturesOn) }) : null,
    holding.closedOn ? t('holdings.closed_on', { date: formatLocalDate(holding.closedOn) }) : null,
  ].filter(Boolean);
  return <span className="text-sm text-muted-foreground">{parts.join(' · ')}</span>;
}

/** A breakdown's parts in the fixed order, as the factsheet gave them. */
function Parts({ label, parts, codes, kind }: { label: string; parts: { code: string; pct: number }[]; codes: readonly string[]; kind: 'region' | 'sector' }) {
  const { t } = useTranslation();
  return (
    <div>
      <h3 className="text-sm font-medium">{label}</h3>
      <ul className="text-sm">
        {codes.map((c) => parts.find((p) => p.code === c)).filter((p) => p !== undefined).map((p) => (
          <li key={p.code} className="flex justify-between gap-4"><span>{(kind === 'region' ? regionText : sectorText)(t, p.code)}</span><span className="tabular-nums">{t('investments.pct', { value: sharePct(p.pct) })}</span></li>
        ))}
      </ul>
    </div>
  );
}
