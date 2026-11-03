import { keepPreviousData } from '@tanstack/react-query';
import { Link, useParams, useSearch } from '@tanstack/react-router';
import { AlertTriangle, Plus } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { IncomeDetailSearch, RootSearch } from '@/app/router';
import { AccessBadge } from '@/components/AccessBadge';
import { FormDialog } from '@/components/FormDialog';
import { formatLocalDate } from '@/components/LocalDate';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { SharingBadges } from '@/components/ownership/SharingBadges';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { SOURCE, SOURCE_OVERVIEW } from './api';
import { EndDialog } from './closing/EndDialog';
import { EndedBanner } from './closing/EndedBanner';
import { CHANGE_AMOUNT_ID, ChangeAmountAction } from './detail/ChangeAmountAction';
import { ChangesTimeline } from './detail/ChangesTimeline';
import { HistoryTab } from './detail/HistoryTab';
import { LastOneOffs } from './detail/LastOneOffs';
import { LastPayments } from './detail/LastPayments';
import { SourceChart } from './detail/SourceChart';
import { SourceFigures } from './detail/SourceFigures';
import { kindText } from './format';
import { followingPhase, groupPhases, newPhase, phaseValuesOf, type PhaseValues } from './phases/phase';
import { PhaseChangeForm } from './phases/PhaseChangeForm';
import { PhaseForm } from './phases/PhaseForm';
import { PhaseRowActions } from './phases/PhaseRowActions';
import { OneOffForm } from './oneoffs/OneOffForm';
import { OneOffsSection } from './oneoffs/OneOffsSection';
import { Section } from '@/components/page/Section';
import { SettingsForm } from './settings/SettingsForm';
import '@/locales/income';
import { ItemDocuments } from '@/features/documents/ItemDocuments';
import '@/locales/documents';

const TABS = ['overview', 'history', 'oneoffs', 'documents', 'settings'] as const;

/**
 * Income dashboard §4: a source's key figures under the title, its gap warnings, then the overview (evolution, last
 * payments and one-offs, the dated changes with each phase's menu), the history, the one-offs and the settings. The page
 * owns the phase and one-off dialogs, the header's « Changer le montant », « + Exceptionnel » and `⋯` (add a component,
 * end the source), and the status region (rendered from the first render).
 */
export function IncomeSourceDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/income/$id' });
  const answer = $api.useQuery('get', SOURCE, { params: { path: { id } } });
  const search: RootSearch & IncomeDetailSearch = useSearch({ strict: false });
  // The figures stay up while the chart's period reloads.
  const overview = $api.useQuery('get', SOURCE_OVERVIEW, { params: { path: { id }, query: { period: search.period ?? '12m' } } }, { placeholderData: keepPreviousData });
  const [tab, setTab] = useDetailTab(TABS);

  // One open dialog at a time: adding (keyed, so a second « Renseigner la suite » remounts it with its own prefill),
  // changing / correcting a phase (from its row or the header: `returnTo` is where the focus goes back unsaved), or a one-off.
  const [adding, setAdding] = useState<{ key: number; values: PhaseValues } | null>(null);
  const [editing, setEditing] = useState<{ id: string; mode: 'correct' | 'change'; returnTo: string } | null>(null);
  const [addingOneOff, setAddingOneOff] = useState(false);
  const [focusId, setFocusId] = useState<string | null>(null);
  const [settingsAnnouncement, setSettingsAnnouncement] = useState('');
  const headingRef = useRef<HTMLHeadingElement>(null);
  const menuRef = useRef<HTMLButtonElement>(null);
  // The page's live region: phases, one-offs, ending and reopening. Cleared the instant an action starts, filled by its outcome.
  const [statusAnnouncement, setStatusAnnouncement] = useState('');

  // After a save the « Changements » heading takes the focus (the rows are about to change); without it on screen, the menu.
  const announce = (message: string) => { setStatusAnnouncement(message); (headingRef.current ?? menuRef.current)?.focus(); };
  const startAdd = (values: PhaseValues) => { setStatusAnnouncement(''); setEditing(null); setAddingOneOff(false); setAdding((prev) => ({ key: (prev?.key ?? 0) + 1, values })); };
  const closeAdd = (message?: string) => { setAdding(null); if (message) announce(message); else menuRef.current?.focus(); };
  const startEdit = (phaseId: string, mode: 'correct' | 'change', returnTo = `phase-menu-${phaseId}`) => {
    setStatusAnnouncement(''); setAdding(null); setAddingOneOff(false); setEditing({ id: phaseId, mode, returnTo });
  };
  const closeEdit = (message?: string) => {
    const was = editing;
    setEditing(null);
    if (message) announce(message); else if (was) setFocusId(was.returnTo);
  };
  const startOneOff = () => { setStatusAnnouncement(''); setAdding(null); setEditing(null); setAddingOneOff(true); };
  // The dialog gives the focus back to the button that opened it; the outcome goes to the live region.
  const closeOneOff = (message?: string) => { setAddingOneOff(false); if (message) setStatusAnnouncement(message); };
  // Ending and reopening: the page owns the announcement (the banner comes and goes with the state) and where the focus
  // lands once the refetch has swapped it — the banner after ending, « Changements » after reopening; never <body>.
  const [endOpen, setEndOpen] = useState(false);
  const [landOn, setLandOn] = useState<'banner' | 'phases' | null>(null);
  const bannerRef = useRef<HTMLParagraphElement>(null);
  const endWasOpen = useRef(false);
  const isEnded = answer.data?.ended;

  const closeEnd = (message?: string) => {
    setEndOpen(false);
    if (message) { setStatusAnnouncement(message); setLandOn('banner'); }
  };
  // Back to the `⋯` menu when the dialog closes without ending (Escape, « Annuler »).
  useEffect(() => {
    if (endOpen) endWasOpen.current = true;
    else if (endWasOpen.current) { endWasOpen.current = false; if (!landOn) menuRef.current?.focus(); }
  }, [endOpen, landOn]);
  // A source that turns ended closes every dialog still open on the page; the focus they held moves to the banner.
  useEffect(() => {
    if (isEnded && (adding || editing || addingOneOff)) { setAdding(null); setEditing(null); setAddingOneOff(false); setLandOn('banner'); }
  }, [isEnded, adding, editing, addingOneOff]);
  useEffect(() => {
    if (landOn === 'banner' && isEnded) { bannerRef.current?.focus(); setLandOn(null); }
    else if (landOn === 'phases' && isEnded === false) { (headingRef.current ?? menuRef.current)?.focus(); setLandOn(null); }
  }, [landOn, isEnded]);
  // « Annuler » brings the focus back to the row's menu, which exists again once the dialog is gone.
  useEffect(() => {
    if (!focusId) return;
    (document.getElementById(focusId) ?? headingRef.current)?.focus();
    setFocusId(null);
  }, [focusId]);

  const source = answer.data;
  const back = <Link to="/income" className="text-sm underline">{t('income_detail.back')}</Link>;
  // Mounted from the first render, loading included, so the text written later is announced.
  const status = <p role="status" aria-live="polite">{statusAnnouncement}</p>;
  if (answer.error) return <article className="space-y-6">{back}{status}<ProblemMessage problem={present(toProblem(answer.error))} /></article>;
  if (!source) return <article className="space-y-6">{back}{status}<div aria-busy="true" aria-label={t('income_detail.loading')} className="h-40 shimmer rounded-md border bg-muted" /></article>;

  // Every write affordance: the header actions, the row menus and the settings.
  const writable = source.canWrite && !source.ended;
  // One phase per label can change: its running one, else its latest upcoming one.
  const changeable = groupPhases(source.phases).map((g) => g.phases[0]!).filter((p) => p.status === 'running' || p.status === 'upcoming');
  const editingPhase = writable && editing ? source.phases.find((p) => p.id === editing.id) : undefined;
  const shown = writable || tab !== 'settings' ? tab : 'overview';

  const actions = writable ? (
    <>
      <ChangeAmountAction phases={changeable} onChange={(phaseId) => startEdit(phaseId, 'change', CHANGE_AMOUNT_ID)} />
      <Button type="button" variant="outline" aria-label={t('income_detail.one_offs.add')} onClick={startOneOff}><Plus aria-hidden="true" />{t('income_detail.add_one_off')}</Button>
      <SharingMenu item={id} onAnnounce={setStatusAnnouncement} shared={source.visibility === 'shared'} label={t('income_detail.more_actions')} triggerRef={menuRef}
        items={[
          { label: t('income_detail.add_component'), onSelect: () => startAdd(newPhase()) },
          { label: t('income_detail.ending.open'), onSelect: () => { setStatusAnnouncement(''); setEndOpen(true); } },
        ]} />
    </>
  ) : source.canWrite ? <SharingMenu item={id} onAnnounce={setStatusAnnouncement} shared={source.visibility === 'shared'} /> : null;

  return (
    <article className="space-y-6">
      <PageHeader crumbs={[{ label: t('nav.income'), to: '/income' }]} title={source.displayName} actions={actions}
        meta={(
          <>
            <span className="text-sm text-muted-foreground">{kindText(t, source.kind)}</span>
            {source.payerLabel ? <span className="text-sm text-muted-foreground">{source.payerLabel}</span> : null}
            <AccessBadge access={source.visibility === 'private' ? 'private' : 'shared'} /><SharingBadges item={id} />
          </>
        )}>
        {overview.data ? <SourceFigures figures={overview.data.figures} />
          : overview.error ? null : <div aria-busy="true" aria-label={t('income_detail.figures_loading')} className="h-16 shimmer rounded-md bg-muted" />}
      </PageHeader>
      {status}
      {source.canWrite ? null : <p>{t('income_detail.read_only')}</p>}
      {source.ended ? (
        <EndedBanner source={source} bannerRef={bannerRef}
          onStart={() => setStatusAnnouncement('')}
          onReopened={(message) => { setStatusAnnouncement(message); setLandOn('phases'); }} />
      ) : null}
      {source.gapSoon.map((gap) => (
        <p key={gap.label} role="note" className="flex flex-wrap items-center gap-2">
          <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
          {t('income_detail.gap', { label: gap.label, date: formatLocalDate(gap.endsOn) })}
          {writable ? (
            <Button type="button" variant="outline" size="sm" onClick={() => startAdd(followingPhase(gap, source.phases))}>{t('income_detail.fill_next')}</Button>
          ) : null}
        </p>
      ))}

      {writable && adding ? (
        <FormDialog key={adding.key} title={t('income_detail.add_phase')} onClose={() => closeAdd()} onDone={closeAdd}>
          {({ titleId, done }) => <PhaseForm source={source} initial={adding.values} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
      {editingPhase && editing ? (
        <FormDialog title={editing.mode === 'change' ? t('income_detail.change.title') : t('income_detail.correct_phase')} onClose={() => closeEdit()} onDone={closeEdit}>
          {({ titleId, done }) => editing.mode === 'change'
            ? <PhaseChangeForm key={`${editingPhase.id}-${editingPhase.version}`} source={source} phase={editingPhase} titleId={titleId} onDone={done} />
            : <PhaseForm key={`${editingPhase.id}-${editingPhase.version}`} source={source} editing={editingPhase} initial={phaseValuesOf(editingPhase)} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
      {writable && addingOneOff ? (
        <FormDialog title={t('income_detail.one_offs.add_title')} onClose={() => closeOneOff()} onDone={closeOneOff}>
          {({ titleId, done }) => <OneOffForm source={source} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
      {endOpen ? <EndDialog source={source} onClose={() => closeEnd()} onDone={closeEnd} /> : null}

      <DetailTabs value={shown} onChange={setTab} tabs={[
        {
          id: 'overview', label: t('income_detail.tab.overview'),
          content: (
            <>
              {overview.data ? (
                <>
                  <SourceChart months={overview.data.months} changes={overview.data.changes} />
                  <div className="grid gap-4 md:grid-cols-2">
                    <LastPayments items={overview.data.lastPayments} />
                    <LastOneOffs items={overview.data.lastOneOffs} onAdd={writable ? startOneOff : undefined} />
                  </div>
                </>
              ) : overview.error ? <ProblemMessage problem={present(toProblem(overview.error))} />
                : <div aria-busy="true" aria-label={t('income_detail.loading')} className="h-64 shimmer rounded-md border bg-muted" />}
              <Section title={t('income_detail.timeline.title')} headingRef={headingRef}>
                {source.phases.length === 0
                  ? <p className="text-muted-foreground">{t('income_detail.no_phases')}</p>
                  : overview.data ? (
                    <ChangesTimeline changes={overview.data.changes} phases={source.phases}
                      actions={writable ? (p) => (
                        <PhaseRowActions source={source} phase={p} onEdit={() => startEdit(p.id, 'correct')}
                          onChangeAmount={p.status === 'running' || p.status === 'upcoming' ? () => startEdit(p.id, 'change') : undefined}
                          onDeleteStarted={() => setStatusAnnouncement('')} onDeleted={announce} />
                      ) : undefined} />
                  ) : null}
              </Section>
            </>
          ),
        },
        { id: 'history', label: t('income_detail.tab.history'), content: <HistoryTab source={source} /> },
        { id: 'oneoffs', label: t('income_detail.tab.oneoffs'), content: <OneOffsSection source={source} writable={source.canWrite} /> },
        { id: 'documents' as const, label: t('documents_item.title'), content: <ItemDocuments itemId={source.id} canWrite={source.canWrite} defaultRole="payslip" /> },
        ...(writable ? [{
          id: 'settings' as const, label: t('income_detail.tab.settings'),
          content: (
            <Section title={t('income_detail.settings.title')} announcement={settingsAnnouncement}>
              <SettingsForm source={source} onStart={() => setSettingsAnnouncement('')} onDone={setSettingsAnnouncement} />
            </Section>
          ),
        }] : []),
      ]} />
    </article>
  );
}
