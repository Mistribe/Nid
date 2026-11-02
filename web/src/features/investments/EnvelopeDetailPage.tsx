import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle, Plus } from 'lucide-react';
import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { todayIso } from '../subscriptions/today';
import { SourceLink } from '../documents/SourceLink';
import { ENVELOPE, HOLDINGS, PERFORMANCE, type Flow, type Valuation } from './api';
import { ValueChart } from './chart/ValueChart';
import { PeriodPerformance } from './period/PeriodPerformance';
import { presetRange } from './period/periods';
import { FlowDialog, ValueDialog } from './entries/EntryDialogs';
import { RowActions } from './entries/RowActions';
import { CloseDialog } from './closing/CloseDialog';
import { ClosedBanner } from './closing/ClosedBanner';
import { SettingsForm } from './settings/SettingsForm';
import { TransferDialog } from './transfer/TransferDialog';
import { AnnualReturn, Direction, Gain, StaleBadge } from './Figures';
import { directionOf, flowKindText, kindText } from './format';
import { percent } from './holdings/holdingFormat';
import { SaleTaxSection } from '@/features/tax/SaleTaxSection';
import '@/locales/investments';
import { ItemDocuments } from '@/features/documents/ItemDocuments';
import '@/locales/documents';
import { ASSOCIABLE, dayMonth, useBankAccounts, useBankingStatus } from '@/features/settings/banks/api';
import '@/locales/banks';

// The « Supports » tab and its dialogs load with the tab: most envelopes have no holdings.
const HoldingsTab = lazy(() => import('./holdings/HoldingsTab'));
// « Opérations » and its import dialog load with the tab, shown once bank sync is on (statement files need no Enable Banking) on an
// envelope a bank account goes with.
const OperationsTab = lazy(() => import('./operations/OperationsTab'));

const TABS = ['overview', 'values', 'flows', 'holdings', 'operations', 'documents', 'settings'] as const;
const newestFirst = <T,>(rows: T[], date: (row: T) => string) =>
  [...rows].sort((a, b) => (date(a) < date(b) ? 1 : date(a) > date(b) ? -1 : 0));

/**
 * 2B2a design §6, as a tabbed page: the key figures inline under the title, the overview (curve, last values,
 * period performance), the values and flows tables newest first, the settings. The entry forms are dialogs, opened
 * from the header or a row's ⋯ menu, shown only when `canWrite` and the envelope is open. A correction gets a new id
 * and a deletion removes the row entirely, so neither can carry its own confirmation once the refetch lands: the page
 * announces it in that table's section and moves focus to the section's heading instead.
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
  const back = <Link to="/investments" className="text-sm underline">{t('envelope.back')}</Link>;
  // The dialogs: a new value or flow (`{}`), the correction of a row, or a new flow on « Versements antérieurs ».
  const [valueDialog, setValueDialog] = useState<{ editing?: Valuation } | null>(null);
  const [flowDialog, setFlowDialog] = useState<{ editing?: Flow; opening?: boolean } | null>(null);
  const [valuesAnnouncement, setValuesAnnouncement] = useState('');
  const [flowsAnnouncement, setFlowsAnnouncement] = useState('');
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
  // Closing and reopening: the page owns the announcement (the banner and the write actions come and go with
  // the state), and where the focus lands once the refetch has swapped them — never <body>.
  const [closeOpen, setCloseOpen] = useState(false);
  const closeWasOpen = useRef(false);
  const [statusAnnouncement, setStatusAnnouncement] = useState('');
  // The « Réglages » status region: cleared when a save starts, filled by its outcome (no focus move: the form stays).
  const [settingsAnnouncement, setSettingsAnnouncement] = useState('');
  const [landOn, setLandOn] = useState<'banner' | 'enter' | null>(null);
  const isClosed = answer.data?.closed;

  // Cleared the instant an action on that section starts (opening a row's correction, confirming a deletion) and
  // set again only on success: an unchanged string never re-triggers the polite live region, so going through
  // empty in between is what makes two successive « Supprimé. » (or a repeat « Flux corrigé. ») each announce, and
  // what keeps a failed correction from reading a previous row's success.
  const clearValues = () => setValuesAnnouncement('');
  const clearFlows = () => setFlowsAnnouncement('');
  const announceValues = (message: string) => { setValuesAnnouncement(message); setFocusTarget('values'); };
  const announceFlows = (message: string) => { setFlowsAnnouncement(message); setFocusTarget('flows'); };
  useEffect(() => {
    if (!focusTarget) return;
    ({ values: valuesHeadingRef, flows: flowsHeadingRef, enter: enterRef, flow: flowRef })[focusTarget].current?.focus();
    setFocusTarget(null);
  }, [focusTarget]);
  useEffect(() => {
    if (!focusRow) return;
    document.getElementById(focusRow)?.focus();
    setFocusRow(null);
  }, [focusRow]);
  const cancelValue = () => { const row = valueDialog?.editing?.id; setValueDialog(null); if (row) setFocusRow(`correct-value-${row}`); };
  const cancelFlow = () => { const row = flowDialog?.editing?.id; setFlowDialog(null); if (row) setFocusRow(`correct-flow-${row}`); };

  // A saved entry: a new one announces in the page's own region and returns to its header button; a corrected row
  // announces in its table's section.
  const savedValue = (editing: boolean) => (message: string) => {
    setValueDialog(null);
    if (editing) announceValues(message); else { setStatusAnnouncement(message); setFocusTarget('enter'); }
  };
  const savedFlow = (editing: boolean) => (message: string) => {
    setFlowDialog(null);
    if (editing) announceFlows(message); else { setStatusAnnouncement(message); setFocusTarget('flow'); }
  };

  // The focus goes back to the ⋯ menu whichever way a dialog opened from it closed (its own `close` already ran by then).
  const focusMenu = () => (menuRef.current ?? enterRef.current ?? bannerRef.current)?.focus();
  const openTransfer = () => { setStatusAnnouncement(''); setTransferOpen(true); };
  const closeTransfer = (message?: string) => {
    setTransferOpen(false);
    if (message) setStatusAnnouncement(message);
  };
  useEffect(() => {
    if (transferOpen) transferWasOpen.current = true;
    else if (transferWasOpen.current) { transferWasOpen.current = false; focusMenu(); }
  }, [transferOpen]);

  const closeClose = (message?: string) => {
    setCloseOpen(false);
    if (message) { setStatusAnnouncement(message); setLandOn('banner'); }
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

  if (answer.error) return <>{back}<ProblemMessage problem={present(toProblem(answer.error))} /></>;
  if (!answer.data) return <>{back}<div aria-busy="true" aria-label={t('envelope.loading')} className="h-40 shimmer rounded-md border bg-muted" /></>;

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

  const actions = writable ? (
    <>
      <Button ref={enterRef} data-guide="investments.enter" type="button" onClick={() => setValueDialog({})}>
        <Plus aria-hidden="true" />{t('envelope.enter_value')}
      </Button>
      <Button ref={flowRef} type="button" variant="outline" onClick={() => setFlowDialog({})}>{t('envelope.add_flow_open')}</Button>
      <SharingMenu item={id} onAnnounce={setStatusAnnouncement} shared={detail.visibility === 'shared'} label={t('envelope.more_actions')} triggerRef={menuRef} items={[
        { label: t('envelope.transfer.open'), onSelect: openTransfer },
        { label: t('envelope.closing.open'), onSelect: () => { setStatusAnnouncement(''); setCloseOpen(true); } },
      ]} />
    </>
  ) : detail.canWrite ? <SharingMenu item={id} onAnnounce={setStatusAnnouncement} shared={detail.visibility === 'shared'} /> : null;

  return (
    <article className="space-y-6">
      <PageHeader crumbs={[{ label: t('nav.investments'), to: '/investments' }]} title={detail.displayName} actions={actions}
        meta={<><span className="text-sm text-muted-foreground">{kindText(t, detail.kind)}</span><AccessBadge access={detail.visibility === 'private' ? 'private' : 'shared'} /></>}>
        <section aria-label={t('envelope.figures')} data-guide="investments.figures" className="space-y-2">
          <StatLine items={[
            { label: figures.asOf ? t('envelope.value_at', { date: formatLocalDate(figures.asOf) }) : t('envelope.value'),
              value: figures.value ? <><Money value={figures.value} tabular />{' '}<StaleBadge days={figures.staleDays} /></> : absent },
            { label: t('envelope.net_contributions'), value: figures.netContributions ? <Money value={figures.netContributions} tabular /> : absent },
            { label: t('envelope.gain'), value: <Gain figures={figures} /> },
            ...(figures.annualReturnPct != null ? [{ label: t('envelope.annual_return'), value: <AnnualReturn value={figures.annualReturnPct} /> }] : []),
            ...(sinceJanuary.data?.performance ? [{ label: t('envelope.since_january'), value: <Direction amount={sinceJanuary.data.performance} /> }] : []),
            ...(holdings.data?.estimate?.value && holdings.data.estimate.asOf ? [{
              label: t('holding_prices.estimate_at', { date: formatLocalDate(holdings.data.estimate.asOf) }),
              value: <><Money value={holdings.data.estimate.value} tabular />{holdings.data.estimate.converted ? <span className="ml-2 text-sm font-normal text-muted-foreground">{t('holding_prices.estimate_converted')}</span> : null}</>,
            }] : []),
            ...extra(t('envelope.fees'), figures.fees),
            ...extra(t('envelope.gain_after_fees'), figures.gainAfterFees),
            ...extra(t('envelope.income'), figures.income),
            ...(holdings.data?.ucSharePct != null ? [{ label: t('holdings.uc_share'), value: (
              <span className="inline-flex items-center gap-1">
                {t('investments.pct', { value: percent(holdings.data.ucSharePct) })}
                {holdings.data.warnings.some((w) => w.code === 'uc_share_below_floor') ? (
                  <span className="inline-flex items-center gap-1 text-sm font-normal">
                    <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
                    {t('holdings.uc_below_floor', { floor: percent(holdings.data.ucShareFloorPct ?? 0) })}
                  </span>
                ) : null}
              </span>
            ) }] : []),
            ...(holdings.data?.explicitFees && directionOf(holdings.data.explicitFees.amount) !== 'flat'
              ? [{ label: t('holdings.fees_taken'), value: <Money value={holdings.data.explicitFees} tabular /> }] : []),
          ]} />
          <p className="text-sm text-muted-foreground">{t('envelope.before_tax')}</p>
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
      <p role="status" aria-live="polite">{statusAnnouncement}</p>
      {detail.closed ? (
        <ClosedBanner envelope={detail} bannerRef={bannerRef}
          onStart={() => setStatusAnnouncement('')}
          onReopened={(message) => { setStatusAnnouncement(message); setLandOn('enter'); }} />
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
              <Section title={t('envelope.evolution')} guide="investments.evolution">
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
                  {values.length > 3 ? <Button type="button" variant="link" size="sm" className="px-0" onClick={() => setTab('values')}>{t('envelope.see_all_values')}</Button> : null}
                </Section>
                <Section title={t('envelope.period.title')}>
                  <PeriodPerformance envelopeId={detail.id} />
                </Section>
              </div>
              <SaleTaxSection item={detail.id} canWrite={detail.canWrite} />
            </>
          ),
        },
        {
          id: 'values', label: t('envelope.tab.values', { count: values.length }),
          content: (
            <Section title={t('envelope.values')} srTitle headingRef={valuesHeadingRef} announcement={valuesAnnouncement}>
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
                        <TableCell><RowActions kind="value" envelope={detail} item={v} onEdit={() => { clearValues(); setValueDialog({ editing: v }); }} onDeleteStarted={clearValues} onDeleted={announceValues} /></TableCell>
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
            <Section title={t('envelope.flows')} srTitle headingRef={flowsHeadingRef} announcement={flowsAnnouncement}>
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
                        <TableCell><RowActions kind="flow" envelope={detail} item={f} onEdit={() => { clearFlows(); setFlowDialog({ editing: f }); }} onDeleteStarted={clearFlows} onDeleted={announceFlows} /></TableCell>
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
            <Section title={t('envelope.settings.title')} srTitle announcement={settingsAnnouncement}>
              <SettingsForm envelope={detail} onStart={() => setSettingsAnnouncement('')} onDone={setSettingsAnnouncement} />
            </Section>
          ),
        }] : []),
      ]} />
    </article>
  );
}
