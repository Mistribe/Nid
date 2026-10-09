import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch, RoutineSearch } from '@/app/router';
import { ProblemMessage } from '@/components/ProblemMessage';
import { monthLabel } from '../income/expected/months';
import { todayIso } from '../subscriptions/today';
import { ROUTINE, type RoutineRecap } from './api';
import { defaultRoutineDate } from './dates';
import { clearDraft, loadDraft } from './draft';
import { RoutineRecapPanel } from './RoutineRecap';
import { RoutineForm, startValues, type RoutineValues } from './RoutineForm';

/** screens.md §5 / 2E2 spec §3: the monthly routine. */
export function RoutinePage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: RootSearch & RoutineSearch = useSearch({ strict: false });
  const today = todayIso();
  const on = search.on ?? defaultRoutineDate(today);
  const [year = 0, month = 1] = on.split('-').map(Number);
  const monthText = monthLabel(year, month);
  const answer = $api.useQuery('get', ROUTINE, { params: { query: { on } } });
  const [saved, setSaved] = useState<{ recap: RoutineRecap; values: RoutineValues } | null>(null);

  const [restored, setRestored] = useState<RoutineValues | null>(null);
  const [undone, setUndone] = useState(false);
  // After « Annuler » the button is gone: focus lands on the status.
  const focusStatus = useCallback((el: HTMLElement | null) => el?.focus(), []);

  const onSaved = (recap: RoutineRecap, values: RoutineValues) => { clearDraft(on); setUndone(false); setSaved({ recap, values }); };

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold">{t('routine.title', { month: monthText })}</h1>
      <label className="flex items-center gap-2 text-sm">
        <span>{t('routine.date')}</span>
        <input type="date" value={on} max={today} className="rounded-md border px-3 py-2"
          onChange={(e) => {
            if (!e.target.value) return;
            setSaved(null); setRestored(null); setUndone(false);
            void navigate({ to: '/routine', replace: true,
              search: (prev: RootSearch & RoutineSearch) => ({ ...prev, on: e.target.value === defaultRoutineDate(today) ? undefined : e.target.value }) });
          }} />
      </label>
      {answer.error && !answer.data ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {!answer.data && !answer.error ? <div role="status" aria-busy="true" aria-label={t('routine.loading')} className="h-48 animate-pulse rounded-md border bg-muted" /> : null}
      {answer.data && answer.data.lines.length === 0 ? (
        <p>{t('routine.none')} <Link to="/investments/new" className="underline">{t('routine.create_envelope')}</Link></p>
      ) : null}
      {answer.data && answer.data.lines.length > 0 && !saved ? (
        <RoutineForm key={`${on}:${restored ? 'restored' : 'fresh'}:${answer.data.lines.map((l) => l.id).join(',')}`} view={answer.data} month={monthText} initial={startValues(answer.data, restored ?? loadDraft(on))} onSaved={onSaved} />
      ) : null}
      {saved ? (
        <RoutineRecapPanel recap={saved.recap} onUndone={() => { setRestored(saved.values); setSaved(null); setUndone(true); }} />
      ) : null}
      {undone && !saved ? <p role="status" tabIndex={-1} ref={focusStatus}>{t('routine.undone')}</p> : null}
    </div>
  );
}
