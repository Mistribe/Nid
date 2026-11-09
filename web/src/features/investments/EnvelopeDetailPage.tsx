import { useParams } from '@tanstack/react-router';
import { AlertTriangle, Plus } from 'lucide-react';
import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { OwnerChips } from '@/components/OwnerChips';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { shownOr } from '@/components/page/RowMenu';
import { PageTrail } from '@/components/page/trail';
import { SharingBadges } from '@/components/ownership/SharingBadges';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { StatLine } from '@/components/page/StatLine';
import { ErrorPage } from '@/components/ErrorPage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { SeriesLink } from '@/components/page/SeriesLink';
import { todayIso } from '@/lib/today';
import { SourceLink } from '../documents/SourceLink';
import { ENVELOPE, HOLDINGS, PERFORMANCE, type Flow, type Valuation } from '@/api/investments';
import { ValueChart } from './chart/ValueChart';
import { PeriodPerformance } from './period/PeriodPerformance';
import { presetRange } from './period/periods';
import { FlowDialog, ValueDialog } from './entries/EntryDialogs';
import { RowActions } from './entries/RowActions';
import { CloseDialog } from './closing/CloseDialog';
import { ClosedBanner } from './closing/ClosedBanner';
import { SettingsForm } from './settings/SettingsForm';
import { TransferDialog } from './transfer/TransferDialog';
import { AnnualReturn, Gain, GainPct, StaleBadge } from './Figures';
import { Direction } from '@/components/Direction';
import { realReturnItem } from './realReturn';
import { directionOf } from '@/lib/amount';
import { flowKindText, kindText } from './format';
import { percent } from './holdings/holdingFormat';
import { SaleTaxSection } from '@/features/tax/SaleTaxSection';
import '@/locales/investments';
import { ItemDocuments } from '@/features/documents/ItemDocuments';
import '@/locales/documents';
import { ASSOCIABLE, dayMonth, useBankAccounts, useBankingStatus } from '@/api/banking';
import '@/locales/banks';
import { PageSkeleton } from '@/components/page/skeletons';

// The « Supports » tab and its dialogs load with the tab: most envelopes have no holdings.
const HoldingsTab = lazy(() => import('./holdings/HoldingsTab'));
// « Opérations » and its import dialog load with the tab, shown once bank sync is on (statement files need no Enable Banking) on an
// envelope a bank account goes with.
const OperationsTab = lazy(() => import('./operations/OperationsTab'));

const TABS = ['overview', 'values', 'flows', 'holdings', 'operations', 'documents', 'settings'] as const;
const MILESTONES = ['life_insurance_8y', 'pea_5y', 'per_retirement'] as const;
const SALE_TAX = 'impot-si-vente';
const newestFirst = <T,>(rows: T[], date: (row: T) => string) =>
  [...rows].sort((a, b) => (date(a) < date(b) ? 1 : date(a) > date(b) ? -1 : 0));

/**
 * 2B2a design §6, as a tabbed page: the key figures inline under the title, the overview (curve, last values,
 * period performance), the values and flows tables newest first, the settings. The entry forms are dialogs, opened
 * from the header or a row's ⋯ menu, shown only when `canWrite` and the envelope is open. A correction gets a new id
 * and a deletion removes the row entirely, so neither can carry its own confirmation once the refetch lands: the page
 * toasts it and moves focus to that table's section heading instead.
 */
export function EnvelopeDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/investments/$id' });
  const answer = $api.useQuery('get', ENVELOPE, { params: { path: { id } } });
  // « Depuis janvier » is the year-to-date performance: the period figure the overview's block asks for too.
  const sinceJanuary = $api.useQuery('get', PERFORMANCE, { params: { path: { id }, query: presetRange('ytd', todayIso()) } });
  // Read here too for the « Part UC » and « Frais prélevés » figures; the tab shares this query.
  const holdings = $api.useQuery('get', HOLDINGS, { params: { path: { id } } });
  // The bank account whose balances become this envelope's values, when bank sync is on.
  const bank = useBankAccounts(id)?.[0];
  const bankingOn = useBankingStatus().data?.enabled === true;
  const [tab, setTab] = useDetailTab(TABS);
  // No `PageHeader` yet: the bar's trail leads back to the list (no inline link of its own).
  const crumbs = [{ label: t('nav.investments'), to: '/investments' as const }];
  const trail = <PageTrail crumbs={crumbs} />;
  // The dialogs: a new value or flow (`{}`), the correction of a row, or a new flow on « Versements antérieurs ».
  const [valueDialog, setValueDialog] = useState<{ editing?: Valuation } | null>(null);
  const [flowDialog, setFlowDialog] = useState<{ editing?: Flow; opening?: boolean } | null>(null);
  const valuesHeadingRef = useRef<HTMLHeadingElement>(null);
  const flowsHeadingRef = useRef<HTMLHeadingElement>(null);
  const enterRef = useRef<HTMLButtonElement>(null);
  const flowRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLButtonElement>(null);
  const bannerRef = useRef<HTMLParagraphElement>(null);
  // Where the focus goes once a dialog handed itself back (a saved entry): the page owns it, never <body>.
  const [focusTarget, setFocusTarget] = useState<'values' | 'flows' | 'enter' | 'flow' | null>(null);
  // The row whose menu should regain focus once its correction dialog is cancelled: that row still exists (unlike
  // after a save, which replaces it).
  const [focusRow, setFocusRow] = useState<string | null>(null);
  const [transferOpen, setTransferOpen] = useState(false);
  const transferWasOpen = useRef(false);
  // Closing and reopening: the page owns the toast (the banner and the write actions come and go with
  // the state), and where the focus lands once the refetch has swapped them — never <body>.
  const [closeOpen, setCloseOpen] = useState(false);
  const closeWasOpen = useRef(false);
  const [landOn, setLandOn] = useState<'banner' | 'enter' | null>(null);
  const isClosed = answer.data?.closed;

  const announceValues = (message: string) => { toast.success(message); setFocusTarget('values'); };
  const announceFlows = (message: string) => { toast.success(message); setFocusTarget('flows'); };
  useEffect(() => {
    if (!focusTarget) return;
    shownOr(({ values: valuesHeadingRef, flows: flowsHeadingRef, enter: enterRef, flow: flowRef })[focusTarget].current, menuRef.current)?.focus();
    setFocusTarget(null);
  }, [focusTarget]);
  useEffect(() => {
    if (!focusRow) return;
    document.getElementById(focusRow)?.focus();
    setFocusRow(null);
  }, [focusRow]);
  const cancelValue = () => { const row = valueDialog?.editing?.id; setValueDialog(null); if (row) setFocusRow(`correct-value-${row}`); };
  const cancelFlow = () => { const row = flowDialog?.editing?.id; setFlowDialog(null); if (row) setFocusRow(`correct-flow-${row}`); };

  // A saved entry: a new one returns the focus to its header button; a corrected row to its table's section.
  const savedValue = (editing: boolean) => (message: string) => {
    setValueDialog(null);
    if (editing) announceValues(message); else { toast.success(message); setFocusTarget('enter'); }
  };
  const savedFlow = (editing: boolean) => (message: string) => {
    setFlowDialog(null);
    if (editing) announceFlows(message); else { toast.success(message); setFocusTarget('flow'); }
  };

  // The focus goes back to the ⋯ menu whichever way a dialog opened from it closed (its own `close` already ran by then).
  const focusMenu = () => (menuRef.current ?? enterRef.current ?? bannerRef.current)?.focus();
  const closeTransfer = (message?: string) => {
    setTransferOpen(false);
    if (message) toast.success(message);
  };
  useEffect(() => {
    if (transferOpen) transferWasOpen.current = true;
    else if (transferWasOpen.current) { transferWasOpen.current = false; focusMenu(); }
  }, [transferOpen]);

  const closeClose = (message?: string) => {
    setCloseOpen(false);
    if (message) { toast.success(message); setLandOn('banner'); }
  };
  useEffect(() => {
    if (closeOpen) closeWasOpen.current = true;
    else if (closeWasOpen.current) { closeWasOpen.current = false; if (!landOn) focusMenu(); }
  }, [closeOpen, landOn]);
  // An entry form open when the envelope turns out closed goes away with its dialog: the focus it held moves to the banner.
  useEffect(() => {
    if (isClosed && (valueDialog || flowDialog)) {
      setValueDialog(null);
      setFlowDialog(null);
      setLandOn('banner');
    }
  }, [isClosed, valueDialog, flowDialog]);
  useEffect(() => {
    if (landOn === 'banner' && isClosed) { bannerRef.current?.focus(); setLandOn(null); }
    else if (landOn === 'enter' && isClosed === false) { (enterRef.current ?? bannerRef.current)?.focus(); setLandOn(null); }
  }, [landOn, isClosed]);

  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <>{trail}<PageSkeleton label={t('envelope.loading')} actions={2} stats={5} tabs={4} /></>;

  const detail = answer.data;
  const figures = detail.figures;
  const values = newestFirst(detail.valuations, (v) => v.asOf);
  const flows = newestFirst(detail.flows, (f) => f.occurredOn);
  const sinceShown = directionOf(figures.contributionsSince.amount) !== 'flat' || directionOf(figures.withdrawalsSince.amount) !== 'flat';
  const absent = <span className="text-base font-normal text-muted-foreground">{t('investments.no_value')}</span>;
  const extra = (label: string, amount: typeof figures.fees) =>
    amount && directionOf(amount.amount) !== 'flat' ? [{ label, value: <Money value={amount} tabular /> }] : [];
  // The missing-opening button offers to add « Versements antérieurs »; hidden once one already exists (finding 9),
  // even if the server still reports `missingOpening` (an opening dated after the latest value).
  const hasOpening = detail.flows.some((f) => f.kind === 'opening');
  // Every write affordance: the header actions, the row menus and the settings.
  const writable = detail.canWrite && !detail.closed;
  const columns = (writable ? 1 : 0) + 3;
  const operations = bankingOn && ASSOCIABLE.includes(detail.kind);
  const shown = (tab === 'settings' && !writable) || (tab === 'operations' && !operations) ? 'overview' : tab;
  const milestone = MILESTONES.find((m) => m === detail.milestone?.code);
  // « Impôt si vente aujourd'hui » lives in the overview tab: open it, then bring the section into view once rendered.
  const showSaleTax = () => {
    setTab('overview');
    requestAnimationFrame(() => document.getElementById(SALE_TAX)?.scrollIntoView({ block: 'start' }));
  };

  const actions = writable ? (
    <>
      <Button ref={enterRef} data-guide="investments.enter" type="button" onClick={() => setValueDialog({})}>
        <Plus aria-hidden="true" />{t('envelope.enter_value')}
      </Button>
      <Button ref={flowRef} type="button" variant="outline" className="max-sm:hidden" onClick={() => setFlowDialog({})}>{t('envelope.add_flow_open')}</Button>
      <SharingMenu item={id} onAnnounce={toast.success} shared={detail.visibility === 'shared'} label={t('page.more_actions')} triggerRef={menuRef} items={[
        { label: t('envelope.add_flow_open'), phone: true, onSelect: () => setFlowDialog({}) },
        { label: t('envelope.transfer.open'), onSelect: () => setTransferOpen(true) },
        { label: t('envelope.closing.open'), onSelect: () => setCloseOpen(true) },
      ]} />
    </>
  ) : detail.canWrite ? <SharingMenu item={id} onAnnounce={toast.success} shared={detail.visibility === 'shared'} label={t('page.more_actions')} /> : null;

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} title={detail.displayName} actions={actions}
        meta={<><span className="text-sm text-muted-foreground">{kindText(t, detail.kind)}</span><AccessBadge access={detail.visibility === 'private' ? 'private' : 'shared'} /><SharingBadges item={id} /><OwnerChips owners={detail.owners} /></>}>
        <section aria-label={t('envelope.figures')} data-guide="investments.figures" className="space-y-2">
          <StatLine items={[
            { label: figures.asOf ? t('envelope.value_at', { date: formatLocalDate(figures.asOf, 'short') }) : t('envelope.value'),
              value: figures.value ? <Money value={figures.value} tabular /> : absent,
              hint: figures.value && figures.staleDays != null ? <StaleBadge days={figures.staleDays} /> : null },
            { label: t('envelope.net_contributions'), value: figures.netContributions ? <Money value={figures.netContributions} tabular /> : absent },
            // #376: the amount on its line, its percent under it.
            figures.gain
              ? { label: t('envelope.gain'), value: <Direction amount={figures.gain} />, hint: <GainPct value={figures.gainPct} /> }
              : { label: t('envelope.gain'), value: <Gain figures={figures} /> },
            ...(figures.annualReturnPct != null ? [{ label: t('envelope.annual_return'), value: <AnnualReturn value={figures.annualReturnPct} /> }] : []),
            ...realReturnItem(t, detail.realReturn),
            ...(sinceJanuary.data?.performance ? [{ label: t('envelope.since_january'), value: <Direction amount={sinceJanuary.data.performance} /> }] : []),
            ...(holdings.data?.estimate?.value && holdings.data.estimate.asOf ? [{
              label: t('holding_prices.estimate_at', { date: formatLocalDate(holdings.data.estimate.asOf, 'short') }),
              value: <Money value={holdings.data.estimate.value} tabular />,
              hint: holdings.data.estimate.converted ? t('holding_prices.estimate_converted') : null,
            }] : []),
            ...extra(t('envelope.fees'), figures.fees),
            ...extra(t('envelope.gain_after_fees'), figures.gainAfterFees),
            ...extra(t('envelope.income'), figures.income),
            ...(holdings.data?.ucSharePct != null ? [{ label: t('holdings.uc_share'),
              value: t('investments.pct', { value: percent(holdings.data.ucSharePct) }),
              hint: holdings.data.warnings.some((w) => w.code === 'uc_share_below_floor') ? (
                <span className="inline-flex items-center gap-1 text-foreground">
                  <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
                  {t('holdings.uc_below_floor', { floor: percent(holdings.data.ucShareFloorPct ?? 0) })}
                </span>
              ) : null }] : []),
            ...(holdings.data?.explicitFees && directionOf(holdings.data.explicitFees.amount) !== 'flat'
              ? [{ label: t('holdings.fees_taken'), value: <Money value={holdings.data.explicitFees} tabular /> }] : []),
            ...(detail.ceiling ? [{ label: t('envelope.regulated.ceiling'), value: formatMoney(detail.ceiling.ceiling), hint: detail.ceiling.reached
              ? t('envelope.regulated.reached')
              : t('envelope.regulated.room', { amount: formatMoney(detail.ceiling.room) }) }] : []),
            ...(detail.interest ? [{ label: t('envelope.regulated.interest_to_date'), value: <Money value={detail.interest.toDate} tabular />,
              hint: t('envelope.regulated.interest_expected', { amount: formatMoney(detail.interest.expected) }) }] : []),
          ]} />
          <p className="text-sm text-muted-foreground">{t('envelope.before_tax')}</p>
          {detail.ceiling ? <p className="text-sm text-muted-foreground">{t('envelope.regulated.ceiling_source', { source: detail.ceiling.source })}</p> : null}
          {detail.interest ? (
            <p className="text-sm text-muted-foreground">
              {t('envelope.regulated.interest_note', { rate: percent(detail.interest.ratePct), source: detail.interest.source })}
            </p>
          ) : null}
          {milestone && detail.milestone ? (
            <p className="text-sm">
              {t(`envelope.milestone.${milestone}`, { date: formatLocalDate(detail.milestone.on, 'short') })}
              {' · '}
              <Button type="button" variant="link" size="sm" className="h-auto p-0" onClick={showSaleTax}>{t('envelope.milestone.sale_link')}</Button>
            </p>
          ) : null}
          {bank ? (
            <p className="text-sm text-muted-foreground">
              {bank.lastBalanceOn ? t('banks.envelope_note.last', { name: bank.name, date: dayMonth(bank.lastBalanceOn) }) : t('banks.envelope_note.linked', { name: bank.name })}
            </p>
          ) : null}
          {bank?.balanceConflictOn ? (
            <p role="note" className="flex items-center gap-2 text-sm">
              <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
              {t('banks.envelope_note.conflict', { date: dayMonth(bank.balanceConflictOn) })}
            </p>
          ) : null}
          {sinceShown ? (
            <p className="text-sm">{t('envelope.since', {
              contributions: formatMoney(figures.contributionsSince),
              withdrawals: formatMoney(figures.withdrawalsSince),
            })}</p>
          ) : null}
        </section>
      </PageHeader>
      {detail.canWrite ? null : <p>{t('envelope.read_only')}</p>}
      {detail.closed ? (
        <ClosedBanner envelope={detail} bannerRef={bannerRef}
          onReopened={(message) => { toast.success(message); setLandOn('enter'); }} />
      ) : null}
      {figures.missingOpening ? (
        <p role="note" className="flex flex-wrap items-center gap-2">
          <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
          {t('envelope.missing_opening')}
          {writable && !hasOpening ? (
            <Button type="button" variant="outline" size="sm" onClick={() => setFlowDialog({ opening: true })}>
              {t('envelope.add_opening')}
            </Button>
          ) : null}
        </p>
      ) : null}

      {/* Outside the tabs: a dialog open when the envelope turns out closed must outlive the write affordances. */}
      {writable && valueDialog ? <ValueDialog envelope={detail} editing={valueDialog.editing} onClose={cancelValue} onDone={savedValue(!!valueDialog.editing)} /> : null}
      {writable && flowDialog ? <FlowDialog envelope={detail} editing={flowDialog.editing} opening={flowDialog.opening} onClose={cancelFlow} onDone={savedFlow(!!flowDialog.editing)} /> : null}
      {transferOpen ? <TransferDialog source={detail} onClose={() => closeTransfer()} onDone={closeTransfer} /> : null}
      {closeOpen ? <CloseDialog envelope={detail} onClose={() => closeClose()} onDone={closeClose} /> : null}

      <DetailTabs value={shown} onChange={setTab} guide="investments.history" tabs={[
        {
          id: 'overview', label: t('envelope.tab.overview'),
          content: (
            <>
              <Section title={t('envelope.evolution')} guide="investments.evolution"
                actions={<SeriesLink keys={[`envelope:${detail.id}:value`, `envelope:${detail.id}:contributions`]} />}>
                <ValueChart envelopeId={detail.id} currency={detail.currency} />
              </Section>
              <div className="grid gap-6 lg:grid-cols-2">
                <Section title={t('envelope.last_values')}>
                  <Table aria-label={t('envelope.last_values')}>
                    <TableBody>
                      {values.length === 0 ? (
                        <TableRow><TableCell className="text-muted-foreground">{t('envelope.no_values')}</TableCell></TableRow>
                      ) : values.slice(0, 3).map((v) => (
                        <TableRow key={v.id}>
                          <TableCell><LocalDate value={v.asOf} /></TableCell>
                          <TableCell className="text-right"><Money value={v.amount} tabular /></TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                  {values.length > 3 ? <Button type="button" variant="link" className="h-auto p-0" onClick={() => setTab('values')}>{t('envelope.see_all_values')}</Button> : null}
                </Section>
                <Section title={t('envelope.period.title')}>
                  <PeriodPerformance envelopeId={detail.id} />
                </Section>
              </div>
              <SaleTaxSection id={SALE_TAX} item={detail.id} canWrite={detail.canWrite} />
            </>
          ),
        },
        {
          id: 'values', label: t('envelope.tab.values', { count: values.length }),
          content: (
            <Section title={t('envelope.values')} srTitle headingRef={valuesHeadingRef}>
              <Table aria-label={t('envelope.values_table')}>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t('envelope.date')}</TableHead>
                    <TableHead>{t('envelope.amount')}</TableHead>
                    <TableHead>{t('envelope.note')}</TableHead>
                    {writable ? <TableHead className="w-0"><span className="sr-only">{t('envelope.actions')}</span></TableHead> : null}
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {values.length === 0 ? (
                    <TableRow><TableCell colSpan={columns} className="text-muted-foreground">{t('envelope.no_values')}</TableCell></TableRow>
                  ) : values.map((v) => (
                    <TableRow key={v.id}>
                      <TableCell><LocalDate value={v.asOf} /></TableCell>
                      <TableCell><Money value={v.amount} tabular /></TableCell>
                      <TableCell className="whitespace-normal">{v.note}<SourceLink documentId={v.documentId} /></TableCell>
                      {writable ? (
                        <TableCell><RowActions kind="value" envelope={detail} item={v} onEdit={() => setValueDialog({ editing: v })} onDeleted={announceValues} /></TableCell>
                      ) : null}
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Section>
          ),
        },
        {
          id: 'flows', label: t('envelope.tab.flows', { count: flows.length }),
          content: (
            <Section title={t('envelope.flows')} srTitle headingRef={flowsHeadingRef}>
              <Table aria-label={t('envelope.flows_table')}>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t('envelope.date')}</TableHead>
                    <TableHead>{t('envelope.kind')}</TableHead>
                    <TableHead>{t('envelope.amount')}</TableHead>
                    <TableHead>{t('envelope.note')}</TableHead>
                    {writable ? <TableHead className="w-0"><span className="sr-only">{t('envelope.actions')}</span></TableHead> : null}
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {flows.length === 0 ? (
                    <TableRow><TableCell colSpan={columns + 1} className="text-muted-foreground">{t('envelope.no_flows')}</TableCell></TableRow>
                  ) : flows.map((f) => (
                    <TableRow key={f.id}>
                      <TableCell><LocalDate value={f.occurredOn} /></TableCell>
                      <TableCell>{flowKindText(t, f)}</TableCell>
                      <TableCell><Money value={f.amount} tabular /></TableCell>
                      <TableCell className="whitespace-normal">{f.note}<SourceLink documentId={f.documentId} /></TableCell>
                      {writable ? (
                        <TableCell><RowActions kind="flow" envelope={detail} item={f} onEdit={() => setFlowDialog({ editing: f })} onDeleted={announceFlows} /></TableCell>
                      ) : null}
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Section>
          ),
        },
        {
          id: 'holdings', label: t('holdings.tab_label'), guide: 'investments.holdings',
          content: (
            <Suspense fallback={<div role="status" aria-busy="true" aria-label={t('holdings.loading')} className="h-40 shimmer rounded-md border bg-muted" />}>
              <HoldingsTab envelope={detail} writable={writable} />
            </Suspense>
          ),
        },
        ...(operations ? [{
          id: 'operations' as const, label: t('banks.operations.title'),
          content: (
            <Suspense fallback={<div role="status" aria-busy="true" aria-label={t('banks.operations.loading')} className="h-40 shimmer rounded-md border bg-muted" />}>
              <OperationsTab envelope={detail} writable={writable} />
            </Suspense>
          ),
        }] : []),
        { id: 'documents' as const, label: t('documents_item.title'), content: <ItemDocuments itemId={detail.id} canWrite={detail.canWrite} defaultRole="statement" /> },
        ...(writable ? [{
          id: 'settings' as const, label: t('envelope.tab.settings'),
          content: (
            <Section title={t('envelope.settings.title')} srTitle>
              <SettingsForm envelope={detail} onDone={toast.success} />
            </Section>
          ),
        }] : []),
      ]} />
    </article>
  );
}
