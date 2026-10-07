import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle, Archive } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { SOURCE } from './api';
import { ExpectedIncome } from './expected/ExpectedIncome';
import { kindText } from './format';
import { followingPhase, groupPhases, newPhase, phaseValuesOf, type PhaseValues } from './phases/phase';
import { PhaseChangeForm } from './phases/PhaseChangeForm';
import { PhaseForm } from './phases/PhaseForm';
import { PhaseRowActions } from './phases/PhaseRowActions';
import { PhasesTable } from './phases/PhasesTable';
import { Section } from './Section';
import { SettingsForm } from './settings/SettingsForm';

/** 2C2a design §7: a source's figures, gap warnings and phases by label. Phase writes (Task 6). */
export function IncomeSourceDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/income/$id' });
  const answer = $api.useQuery('get', SOURCE, { params: { path: { id } } });
  const back = <Link to="/income" className="text-sm underline">{t('income_detail.back')}</Link>;

  // One open form at a time: adding (keyed, so a second « Renseigner la suite » remounts it with its own prefill) or
  // correcting a row. The phases section owns the announcement and where the focus lands.
  const [adding, setAdding] = useState<{ key: number; values: PhaseValues } | null>(null);
  const [editing, setEditing] = useState<{ id: string; mode: 'correct' | 'change' } | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [focusId, setFocusId] = useState<string | null>(null);
  const [settingsAnnouncement, setSettingsAnnouncement] = useState('');
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addButtonRef = useRef<HTMLButtonElement>(null);

  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const startAdd = (values: PhaseValues) => { setAnnouncement(''); setEditing(null); setAdding((prev) => ({ key: (prev?.key ?? 0) + 1, values })); };
  const closeAdd = (message?: string) => { setAdding(null); if (message) announce(message); else addButtonRef.current?.focus(); };
  const startEdit = (phaseId: string, mode: 'correct' | 'change') => { setAnnouncement(''); setAdding(null); setEditing({ id: phaseId, mode }); };
  const closeEdit = (message?: string) => {
    const was = editing;
    setEditing(null);
    if (message) announce(message); else if (was) setFocusId(`${was.mode === 'change' ? 'change' : 'correct'}-phase-${was.id}`);
  };
  // « Annuler » brings the focus back to the button that opened the form, which exists again once the row reverts.
  useEffect(() => {
    if (!focusId) return;
    document.getElementById(focusId)?.focus();
    setFocusId(null);
  }, [focusId]);

  if (answer.error) return <>{back}<ProblemMessage problem={present(toProblem(answer.error))} /></>;
  if (!answer.data) return <>{back}<div aria-busy="true" aria-label={t('income_detail.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></>;

  const source = answer.data;
  const running = source.phases.filter((p) => p.status === 'running');
  const groups = groupPhases(source.phases);
  // Every write affordance: the add button and form, « Renseigner la suite », the row actions, the settings (Task 7).
  const writable = source.canWrite && !source.ended;
  const editingPhase = writable && editing ? source.phases.find((p) => p.id === editing.id) : undefined;

  return (
    <article className="space-y-6">
      {back}
      <header className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold">{source.displayName}</h1>
        <span className="text-sm text-muted-foreground">{kindText(t, source.kind)}</span>
        {source.payerLabel ? <span className="text-sm text-muted-foreground">{source.payerLabel}</span> : null}
        <AccessBadge access={source.visibility === 'private' ? 'private' : 'shared'} />
      </header>
      {source.canWrite ? null : <p>{t('income_detail.read_only')}</p>}
      {source.ended ? (
        <p role="note" className="flex flex-wrap items-center gap-2 rounded-md border p-3">
          <Archive aria-hidden="true" className="size-4" />
          <span className="font-medium">{t('income_detail.ended_banner')}</span>
          {source.endedOn ? <span>{t('income_detail.ended_on', { date: formatLocalDate(source.endedOn) })}</span> : null}
        </p>
      ) : null}

      <Section title={t('income_detail.figures')}>
        <p>
          <span className="tabular-nums">{t('income.per_month', { amount: formatMoney(source.monthlyEquivalent) })}</span>
          {' '}<span className="text-muted-foreground">{t('income_detail.smoothed')}</span>
        </p>
        {running.length === 0 ? <p className="text-muted-foreground">{t('income_detail.no_running')}</p> : (
          <ul aria-label={t('income_detail.running_list')} className="space-y-1">
            {running.map((p) => <li key={p.id}>{t('income_detail.running_part', { label: p.label, amount: formatMoney(p.monthlyEquivalent) })}</li>)}
          </ul>
        )}
      </Section>

      {source.gapSoon.map((gap) => (
        <p key={gap.label} role="note" className="flex flex-wrap items-center gap-2">
          <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
          {t('income_detail.gap', { label: gap.label, date: formatLocalDate(gap.endsOn) })}
          {writable ? (
            <Button type="button" variant="outline" size="sm" onClick={() => startAdd(followingPhase(gap, source.phases))}>{t('income_detail.fill_next')}</Button>
          ) : null}
        </p>
      ))}

      <Section title={t('income_detail.phases')} headingRef={headingRef} announcement={announcement}>
        {writable ? (
          <Button ref={addButtonRef} type="button" variant="outline" onClick={() => startAdd(newPhase())}>{t('income_detail.add_phase')}</Button>
        ) : null}
        {writable && adding ? <PhaseForm key={adding.key} source={source} initial={adding.values} onDone={closeAdd} /> : null}
        {groups.length === 0
          ? <p className="text-muted-foreground">{t('income_detail.no_phases')}</p>
          : groups.map((g) => (
            <PhasesTable key={g.label} label={g.label} phases={g.phases}
              actions={writable ? (p) => (
                <PhaseRowActions source={source} phase={p} onEdit={() => startEdit(p.id, 'correct')}
                  onChangeAmount={p.status === 'running' || p.status === 'upcoming' ? () => startEdit(p.id, 'change') : undefined} onDeleteStarted={() => setAnnouncement('')} onDeleted={announce} />
              ) : undefined}
              editing={editingPhase && editing && editingPhase.label === g.label
                ? {
                  id: editingPhase.id,
                  form: editing.mode === 'change'
                    ? <PhaseChangeForm key={`${editingPhase.id}-${editingPhase.version}`} source={source} phase={editingPhase} onDone={closeEdit} />
                    : <PhaseForm key={`${editingPhase.id}-${editingPhase.version}`} source={source} editing={editingPhase} initial={phaseValuesOf(editingPhase)} onDone={closeEdit} />,
                }
                : null} />
          ))}
      </Section>

      <Section title={t('income_detail.expected.title')}>
        <ExpectedIncome source={source} />
      </Section>

      {writable ? (
        <Section title={t('income_detail.settings.title')} announcement={settingsAnnouncement}>
          <SettingsForm source={source} onStart={() => setSettingsAnnouncement('')} onDone={setSettingsAnnouncement} />
        </Section>
      ) : null}
    </article>
  );
}
