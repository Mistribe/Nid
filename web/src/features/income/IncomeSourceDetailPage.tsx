import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle, Plus } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { FormDialog } from '@/components/FormDialog';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { SOURCE } from './api';
import { EndDialog } from './closing/EndDialog';
import { EndedBanner } from './closing/EndedBanner';
import { ExpectedIncome } from './expected/ExpectedIncome';
import { kindText } from './format';
import { followingPhase, groupPhases, newPhase, phaseValuesOf, type PhaseValues } from './phases/phase';
import { PhaseChangeForm } from './phases/PhaseChangeForm';
import { PhaseForm } from './phases/PhaseForm';
import { PhaseRowActions } from './phases/PhaseRowActions';
import { PhasesTable } from './phases/PhasesTable';
import { OneOffsSection } from './oneoffs/OneOffsSection';
import { Section } from './Section';
import { SettingsForm } from './settings/SettingsForm';

const TABS = ['overview', 'oneoffs', 'settings'] as const;

/**
 * 2C2a design §7, redesign §5.2–5.3: a source's figures under the title, its gap warnings, then the overview (the phases
 * by label with their status, the expected income), the one-offs and the settings. The page owns the phase dialogs, the
 * header's « Ajouter une période » and « Terminer cette source », and the status region (rendered from the first render).
 */
export function IncomeSourceDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/income/$id' });
  const answer = $api.useQuery('get', SOURCE, { params: { path: { id } } });
  const [tab, setTab] = useDetailTab(TABS);

  // One open dialog at a time: adding (keyed, so a second « Renseigner la suite » remounts it with its own prefill) or
  // changing / correcting a row.
  const [adding, setAdding] = useState<{ key: number; values: PhaseValues } | null>(null);
  const [editing, setEditing] = useState<{ id: string; mode: 'correct' | 'change' } | null>(null);
  const [focusId, setFocusId] = useState<string | null>(null);
  const [settingsAnnouncement, setSettingsAnnouncement] = useState('');
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addButtonRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLButtonElement>(null);
  // The page's live region: phases, ending and reopening. Cleared the instant an action starts, filled by its outcome.
  const [statusAnnouncement, setStatusAnnouncement] = useState('');

  // After a save the « Périodes » heading takes the focus (the rows are about to change); without it on screen, the button.
  const announce = (message: string) => { setStatusAnnouncement(message); (headingRef.current ?? addButtonRef.current ?? menuRef.current)?.focus(); };
  const startAdd = (values: PhaseValues) => { setStatusAnnouncement(''); setEditing(null); setAdding((prev) => ({ key: (prev?.key ?? 0) + 1, values })); };
  const closeAdd = (message?: string) => { setAdding(null); if (message) announce(message); else addButtonRef.current?.focus(); };
  const startEdit = (phaseId: string, mode: 'correct' | 'change') => { setStatusAnnouncement(''); setAdding(null); setEditing({ id: phaseId, mode }); };
  const closeEdit = (message?: string) => {
    const was = editing;
    setEditing(null);
    if (message) announce(message); else if (was) setFocusId(`phase-menu-${was.id}`);
  };
  // Ending and reopening: the page owns the announcement (the banner comes and goes with the state) and where the focus
  // lands once the refetch has swapped it — the banner after ending, « Périodes » after reopening; never <body>.
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
    if (isEnded && (adding || editing)) { setAdding(null); setEditing(null); setLandOn('banner'); }
  }, [isEnded, adding, editing]);
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
  if (!source) return <article className="space-y-6">{back}{status}<div aria-busy="true" aria-label={t('income_detail.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></article>;

  const running = source.phases.filter((p) => p.status === 'running');
  const groups = groupPhases(source.phases);
  // Every write affordance: the header actions, the row menus and the settings.
  const writable = source.canWrite && !source.ended;
  const editingPhase = writable && editing ? source.phases.find((p) => p.id === editing.id) : undefined;
  const shown = writable || tab !== 'settings' ? tab : 'overview';

  const actions = writable ? (
    <>
      <Button ref={addButtonRef} type="button" onClick={() => startAdd(newPhase())}><Plus aria-hidden="true" />{t('income_detail.add_phase')}</Button>
      <RowMenu label={t('income_detail.more_actions')} triggerRef={menuRef}
        items={[{ label: t('income_detail.ending.open'), onSelect: () => { setStatusAnnouncement(''); setEndOpen(true); } }]} />
    </>
  ) : null;

  return (
    <article className="space-y-6">
      <PageHeader crumbs={[{ label: t('nav.income'), to: '/income' }]} title={source.displayName} actions={actions}
        meta={(
          <>
            <span className="text-sm text-muted-foreground">{kindText(t, source.kind)}</span>
            {source.payerLabel ? <span className="text-sm text-muted-foreground">{source.payerLabel}</span> : null}
            <AccessBadge access={source.visibility === 'private' ? 'private' : 'shared'} />
          </>
        )}>
        <section aria-label={t('income_detail.figures')} className="space-y-2">
          <StatLine items={[{ label: t('income_detail.figures'), value: t('income.per_month', { amount: formatMoney(source.monthlyEquivalent) }) }]} />
          <p className="text-sm text-muted-foreground">{t('income_detail.smoothed')}</p>
          {running.length === 0 ? <p className="text-muted-foreground">{t('income_detail.no_running')}</p> : (
            <ul aria-label={t('income_detail.running_list')} className="space-y-1 text-sm">
              {running.map((p) => <li key={p.id}>{t('income_detail.running_part', { label: p.label, amount: formatMoney(p.monthlyEquivalent) })}</li>)}
            </ul>
          )}
        </section>
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
      {endOpen ? <EndDialog source={source} onClose={() => closeEnd()} onDone={closeEnd} /> : null}

      <DetailTabs value={shown} onChange={setTab} tabs={[
        {
          id: 'overview', label: t('income_detail.tab.overview'),
          content: (
            <>
              <Section title={t('income_detail.phases')} headingRef={headingRef}>
                {groups.length === 0
                  ? <p className="text-muted-foreground">{t('income_detail.no_phases')}</p>
                  : groups.map((g) => (
                    <PhasesTable key={g.label} label={g.label} phases={g.phases}
                      actions={writable ? (p) => (
                        <PhaseRowActions source={source} phase={p} onEdit={() => startEdit(p.id, 'correct')}
                          onChangeAmount={p.status === 'running' || p.status === 'upcoming' ? () => startEdit(p.id, 'change') : undefined}
                          onDeleteStarted={() => setStatusAnnouncement('')} onDeleted={announce} />
                      ) : undefined} />
                  ))}
              </Section>
              <Section title={t('income_detail.expected.title')}><ExpectedIncome source={source} /></Section>
            </>
          ),
        },
        { id: 'oneoffs', label: t('income_detail.tab.oneoffs'), content: <OneOffsSection source={source} writable={writable} /> },
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
