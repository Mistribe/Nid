import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ENVELOPE } from './api';
import { ValueChart } from './chart/ValueChart';
import { PeriodPerformance } from './period/PeriodPerformance';
import { FlowForm } from './entries/FlowForm';
import { RowActions } from './entries/RowActions';
import { ValueForm } from './entries/ValueForm';
import { CloseDialog } from './closing/CloseDialog';
import { ClosedBanner } from './closing/ClosedBanner';
import { TransferDialog } from './transfer/TransferDialog';
import { Gain, StaleBadge } from './Figures';
import { directionOf, flowKindText, kindText } from './format';

/**
 * 2B2a design §6: the envelope's figures, warnings, and its values and flows, newest first — the quick-value/
 * add-flow forms and each row's « Corriger »/« Supprimer » shown only when `canWrite` (Task 5). A correction gets a
 * new id and a deletion removes the row entirely, so neither can carry its own confirmation once the refetch
 * lands: the page announces it in that table's section and moves focus to the section's heading instead.
 */
export function EnvelopeDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/investments/$id' });
  const answer = $api.useQuery('get', ENVELOPE, { params: { path: { id } } });
  const back = <Link to="/investments" className="text-sm underline">{t('envelope.back')}</Link>;
  const [editingValueId, setEditingValueId] = useState<string | null>(null);
  const [editingFlowId, setEditingFlowId] = useState<string | null>(null);
  // Bumped by the missing-opening button; a counter (not just `'opening'`) so a second click re-applies it even
  // when the person had since changed the kind back, or the request was already 'opening'.
  const [openingRequest, setOpeningRequest] = useState<number | undefined>(undefined);
  const [valuesAnnouncement, setValuesAnnouncement] = useState('');
  const [flowsAnnouncement, setFlowsAnnouncement] = useState('');
  const valuesHeadingRef = useRef<HTMLHeadingElement>(null);
  const flowsHeadingRef = useRef<HTMLHeadingElement>(null);
  const [transferOpen, setTransferOpen] = useState(false);
  const transferWasOpen = useRef(false);
  const enterHeadingRef = useRef<HTMLHeadingElement>(null);
  const transferButtonRef = useRef<HTMLButtonElement>(null);
  // The row whose « Corriger » button should regain focus once « Annuler » closes its edit form (finding 13): that
  // button still exists (the row just reverts), unlike after a save, which removes or replaces the row.
  const [focusValueId, setFocusValueId] = useState<string | null>(null);
  const [focusFlowId, setFocusFlowId] = useState<string | null>(null);
  // Closing and reopening: the page owns the announcement (the banner and the « Saisir » section come and go with
  // the state), and where the focus lands once the refetch has swapped them — never <body>.
  const [closeOpen, setCloseOpen] = useState(false);
  const [statusAnnouncement, setStatusAnnouncement] = useState('');
  const [landOn, setLandOn] = useState<'banner' | 'enter' | null>(null);
  const closeWasOpen = useRef(false);
  const closeButtonRef = useRef<HTMLButtonElement>(null);
  const bannerRef = useRef<HTMLParagraphElement>(null);
  const isClosed = answer.data?.closed;

  // Cleared the instant an action on that section starts (opening a row's correction, confirming a deletion) and
  // set again only on success: an unchanged string never re-triggers the polite live region, so going through
  // empty in between is what makes two successive « Supprimé. » (or a repeat « Flux corrigé. ») each announce, and
  // what keeps a failed correction from reading a previous row's success.
  const clearValues = () => setValuesAnnouncement('');
  const clearFlows = () => setFlowsAnnouncement('');
  const announceValues = (message: string) => { setValuesAnnouncement(message); valuesHeadingRef.current?.focus(); };
  const announceFlows = (message: string) => { setFlowsAnnouncement(message); flowsHeadingRef.current?.focus(); };

  // Opening clears the Flux announcement like the other actions; the dialog's success is announced there, and the
  // focus goes back to the button whichever way the dialog closed (its own `close` already ran by then).
  const openTransfer = () => { clearFlows(); setTransferOpen(true); };
  const closeTransfer = (message?: string) => {
    setTransferOpen(false);
    if (message) setFlowsAnnouncement(message);
  };
  useEffect(() => {
    if (transferOpen) transferWasOpen.current = true;
    else if (transferWasOpen.current) { transferWasOpen.current = false; (transferButtonRef.current ?? enterHeadingRef.current ?? bannerRef.current)?.focus(); }
  }, [transferOpen]);

  const closeClose = (message?: string) => {
    setCloseOpen(false);
    if (message) { setStatusAnnouncement(message); setLandOn('banner'); }
  };
  useEffect(() => {
    if (closeOpen) closeWasOpen.current = true;
    else if (closeWasOpen.current) { closeWasOpen.current = false; if (!landOn) (closeButtonRef.current ?? enterHeadingRef.current ?? bannerRef.current)?.focus(); }
  }, [closeOpen, landOn]);
  // A correction open when the envelope turns out closed goes away with its form: the focus it held moves to the banner.
  useEffect(() => {
    if (isClosed && (editingValueId || editingFlowId)) {
      setEditingValueId(null);
      setEditingFlowId(null);
      setLandOn('banner');
    }
  }, [isClosed, editingValueId, editingFlowId]);
  useEffect(() => {
    if (landOn === 'banner' && isClosed) { bannerRef.current?.focus(); setLandOn(null); }
    else if (landOn === 'enter' && isClosed === false) { enterHeadingRef.current?.focus(); setLandOn(null); }
  }, [landOn, isClosed]);

  const startValueEdit = (id: string) => { clearValues(); setEditingValueId(id); };
  const startFlowEdit = (id: string) => { clearFlows(); setEditingFlowId(id); };
  const closeValueRow = (message?: string) => {
    const id = editingValueId;
    setEditingValueId(null);
    if (message) announceValues(message); else setFocusValueId(id);
  };
  const closeFlowRow = (message?: string) => {
    const id = editingFlowId;
    setEditingFlowId(null);
    if (message) announceFlows(message); else setFocusFlowId(id);
  };
  useEffect(() => {
    if (!focusValueId) return;
    document.getElementById(`correct-value-${focusValueId}`)?.focus();
    setFocusValueId(null);
  }, [focusValueId]);
  useEffect(() => {
    if (!focusFlowId) return;
    document.getElementById(`correct-flow-${focusFlowId}`)?.focus();
    setFocusFlowId(null);
  }, [focusFlowId]);

  if (answer.error) return <>{back}<ProblemMessage problem={present(toProblem(answer.error))} /></>;
  if (!answer.data) return <>{back}<div aria-busy="true" aria-label={t('envelope.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></>;

  const detail = answer.data;
  const figures = detail.figures;
  const values = [...detail.valuations].sort((a, b) => (a.asOf < b.asOf ? 1 : a.asOf > b.asOf ? -1 : 0));
  const flows = [...detail.flows].sort((a, b) => (a.occurredOn < b.occurredOn ? 1 : a.occurredOn > b.occurredOn ? -1 : 0));
  const sinceShown = directionOf(figures.contributionsSince.amount) !== 'flat' || directionOf(figures.withdrawalsSince.amount) !== 'flat';
  const feesShown = !!figures.fees && directionOf(figures.fees.amount) !== 'flat';
  const gainAfterFeesShown = !!figures.gainAfterFees && directionOf(figures.gainAfterFees.amount) !== 'flat';
  const incomeShown = !!figures.income && directionOf(figures.income.amount) !== 'flat';
  // The missing-opening button offers to add « Versements antérieurs »; hidden once one already exists (finding 9),
  // even if the server still reports `missingOpening` (an opening dated after the latest value).
  const hasOpening = detail.flows.some((f) => f.kind === 'opening');
  // Every write affordance: the « Saisir » section, the row actions, the transfer and the close buttons.
  const writable = detail.canWrite && !detail.closed;
  const valueCols = writable ? 4 : 3;
  const flowCols = writable ? 5 : 4;

  return (
    <article className="space-y-6">
      {back}
      <header className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold">{detail.displayName}</h1>
        <span className="text-sm text-muted-foreground">{kindText(t, detail.kind)}</span>
        <AccessBadge access={detail.visibility === 'private' ? 'private' : 'shared'} />
      </header>
      {detail.canWrite ? null : <p>{t('envelope.read_only')}</p>}
      <p role="status" aria-live="polite">{statusAnnouncement}</p>
      {detail.closed ? (
        <ClosedBanner envelope={detail} bannerRef={bannerRef}
          onStart={() => setStatusAnnouncement('')}
          onReopened={(message) => { setStatusAnnouncement(message); setLandOn('enter'); }} />
      ) : null}

      <Section title={t('envelope.figures')}>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
          <dt>{t('envelope.value')}</dt>
          <dd>
            {figures.value ? (
              <>
                <Money value={figures.value} tabular />
                {figures.asOf ? <> · <LocalDate value={figures.asOf} /></> : null}
                {' '}<StaleBadge days={figures.staleDays} />
              </>
            ) : <span className="text-muted-foreground">{t('investments.no_value')}</span>}
          </dd>
          <dt>{t('envelope.net_contributions')}</dt>
          <dd>
            {figures.netContributions
              ? <Money value={figures.netContributions} tabular />
              : <span className="text-muted-foreground">{t('investments.no_value')}</span>}
          </dd>
          <dt>{t('envelope.gain')}</dt>
          <dd><Gain figures={figures} /> <span className="text-muted-foreground">{t('envelope.before_tax')}</span></dd>
          {feesShown && figures.fees ? (
            <>
              <dt>{t('envelope.fees')}</dt>
              <dd><Money value={figures.fees} tabular /></dd>
            </>
          ) : null}
          {gainAfterFeesShown && figures.gainAfterFees ? (
            <>
              <dt>{t('envelope.gain_after_fees')}</dt>
              <dd><Money value={figures.gainAfterFees} tabular /></dd>
            </>
          ) : null}
          {incomeShown && figures.income ? (
            <>
              <dt>{t('envelope.income')}</dt>
              <dd><Money value={figures.income} tabular /></dd>
            </>
          ) : null}
        </dl>
        {sinceShown ? (
          <p>{t('envelope.since', {
            contributions: formatMoney(figures.contributionsSince),
            withdrawals: formatMoney(figures.withdrawalsSince),
          })}</p>
        ) : null}
        {figures.missingOpening ? (
          <p role="note" className="flex flex-wrap items-center gap-2">
            <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
            {t('envelope.missing_opening')}
            {writable && !hasOpening ? (
              <Button type="button" variant="outline" size="sm" onClick={() => setOpeningRequest((n) => (n ?? 0) + 1)}>
                {t('envelope.add_opening')}
              </Button>
            ) : null}
          </p>
        ) : null}
      </Section>

      <Section title={t('envelope.evolution')}>
        <ValueChart envelopeId={detail.id} currency={detail.currency} />
      </Section>

      <Section title={t('envelope.period.title')}>
        <PeriodPerformance envelopeId={detail.id} />
      </Section>

      {writable ? (
        <Section title={t('envelope.enter')} headingRef={enterHeadingRef}>
          <div className="grid gap-6 md:grid-cols-2">
            <ValueForm envelope={detail} />
            <FlowForm envelope={detail} openingRequest={openingRequest} />
          </div>
          <div className="flex flex-wrap gap-2">
            <Button ref={transferButtonRef} type="button" variant="outline" onClick={openTransfer}>
              {t('envelope.transfer.open')}
            </Button>
            <Button ref={closeButtonRef} type="button" variant="outline" onClick={() => { setStatusAnnouncement(''); setCloseOpen(true); }}>
              {t('envelope.closing.open')}
            </Button>
          </div>
        </Section>
      ) : null}

      {/* Outside the « Saisir » section: a dialog open when the envelope turns out closed must outlive it. */}
      {transferOpen ? <TransferDialog source={detail} onClose={() => closeTransfer()} onDone={closeTransfer} /> : null}
      {closeOpen ? <CloseDialog envelope={detail} onClose={() => closeClose()} onDone={closeClose} /> : null}

      <Section title={t('envelope.values')} headingRef={valuesHeadingRef} announcement={valuesAnnouncement}>
        <table className="w-full text-sm" aria-label={t('envelope.values_table')}>
          <thead>
            <tr>
              <th scope="col" className="text-left">{t('envelope.date')}</th>
              <th scope="col" className="text-left">{t('envelope.amount')}</th>
              <th scope="col" className="text-left">{t('envelope.note')}</th>
              {writable ? <th scope="col" className="text-left">{t('envelope.actions')}</th> : null}
            </tr>
          </thead>
          <tbody>
            {values.length === 0 ? (
              <tr><td colSpan={valueCols} className="text-muted-foreground">{t('envelope.no_values')}</td></tr>
            ) : values.map((v) => writable && editingValueId === v.id ? (
              <tr key={v.id}>
                <td colSpan={valueCols}>
                  <ValueForm envelope={detail} editing={v} onDone={closeValueRow} />
                </td>
              </tr>
            ) : (
              <tr key={v.id}>
                <td><LocalDate value={v.asOf} /></td>
                <td><Money value={v.amount} tabular /></td>
                <td>{v.note}</td>
                {writable ? (
                  <td><RowActions kind="value" envelope={detail} item={v} onEdit={() => startValueEdit(v.id)} onDeleteStarted={clearValues} onDeleted={announceValues} /></td>
                ) : null}
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title={t('envelope.flows')} headingRef={flowsHeadingRef} announcement={flowsAnnouncement}>
        <table className="w-full text-sm" aria-label={t('envelope.flows_table')}>
          <thead>
            <tr>
              <th scope="col" className="text-left">{t('envelope.date')}</th>
              <th scope="col" className="text-left">{t('envelope.kind')}</th>
              <th scope="col" className="text-left">{t('envelope.amount')}</th>
              <th scope="col" className="text-left">{t('envelope.note')}</th>
              {writable ? <th scope="col" className="text-left">{t('envelope.actions')}</th> : null}
            </tr>
          </thead>
          <tbody>
            {flows.length === 0 ? (
              <tr><td colSpan={flowCols} className="text-muted-foreground">{t('envelope.no_flows')}</td></tr>
            ) : flows.map((f) => writable && editingFlowId === f.id ? (
              <tr key={f.id}>
                <td colSpan={flowCols}>
                  <FlowForm envelope={detail} editing={f} onDone={closeFlowRow} />
                </td>
              </tr>
            ) : (
              <tr key={f.id}>
                <td><LocalDate value={f.occurredOn} /></td>
                <td>{flowKindText(t, f)}</td>
                <td><Money value={f.amount} tabular /></td>
                <td>{f.note}</td>
                {writable ? (
                  <td><RowActions kind="flow" envelope={detail} item={f} onEdit={() => startFlowEdit(f.id)} onDeleteStarted={clearFlows} onDeleted={announceFlows} /></td>
                ) : null}
              </tr>
            ))}
          </tbody>
        </table>
      </Section>
    </article>
  );
}

function Section({ title, headingRef, announcement, children }: {
  title: string; headingRef?: RefObject<HTMLHeadingElement | null>; announcement?: string; children: ReactNode;
}) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="space-y-2">
      <h2 id={id} ref={headingRef} tabIndex={headingRef ? -1 : undefined} className="text-lg font-semibold">{title}</h2>
      {announcement !== undefined ? <p role="status" aria-live="polite">{announcement}</p> : null}
      {children}
    </section>
  );
}
