import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useCallback, useId, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import type { RootSearch } from '@/lib/search';
import type { RoutineSearch } from '@/app/router';
import { DateField } from '@/components/form/DateField';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { PageHeader } from '@/components/page/PageHeader';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { monthLabel } from '@/lib/format';
import { elision } from '@/lib/elision';
import { ToLinkLink } from '../operations/ToLinkLink';
import { PendingLine } from '../proposals/PendingLine';
import { todayIso } from '@/lib/today';
import { ISO_DATE } from '@/lib/amount';
import { ROUTINE, type RoutineRecap } from './api';
import { defaultRoutineDate } from './dates';
import { clearDraft, loadDraft } from './draft';
import { ChildcareSection } from './ChildcareSection';
import { EstateSection } from './EstateSection';
import { RoutineRecapPanel } from './RoutineRecap';
import { RoutineForm, startValues, type Banked, type RoutineValues } from './RoutineForm';
import { useBankAccounts } from '@/api/banking';

/** screens.md §5 / 2E2 spec §3: the monthly routine. */
export function RoutinePage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { modules } = useSession().capabilities;
  const envelopes = modules.includes('investments');
  const navigate = useNavigate();
  const search: RootSearch & RoutineSearch = useSearch({ strict: false });
  const today = todayIso();
  const on = search.on ?? defaultRoutineDate(today);
  const [year = 0, month = 1] = on.split('-').map(Number);
  const monthText = monthLabel(year, month);
  const answer = $api.useQuery('get', ROUTINE, { params: { query: { on } } }, { enabled: envelopes });
  // The envelopes whose bank account applies its balances and is still read: no value field for them. Known before the form starts.
  const accounts = useBankAccounts();
  const banked = useMemo<Banked | undefined>(() => accounts && Object.fromEntries(accounts
    .filter((a) => a.envelopeId && a.autoApplyBalance && a.synced && !a.ignored && a.lastBalanceOn).map((a) => [a.envelopeId!, a.lastBalanceOn!])), [accounts]);
  // What is typed in the date field until it is a day the routine can open (a past one).
  const [typed, setTyped] = useState<string | null>(null);
  const errorId = useId();
  const [saved, setSaved] = useState<{ recap: RoutineRecap; values: RoutineValues } | null>(null);

  const [restored, setRestored] = useState<RoutineValues | null>(null);
  const [undone, setUndone] = useState(false);
  // After « Annuler » the button is gone: focus lands on the status.
  const focusStatus = useCallback((el: HTMLElement | null) => el?.focus(), []);

  const onSaved = (recap: RoutineRecap, values: RoutineValues) => { clearDraft(on); setUndone(false); setSaved({ recap, values }); };

  return (
    <div className="space-y-6">
      <PageHeader title={t('routine.title', { month: monthText, context: elision(monthText) })} actions={<ToLinkLink />} />
      <PendingLine />
      <FilterRow label={t('common.filters')}>
        <FilterField label={t('routine.date')} labelFor>
          {(labelId, controlId) => (
            <div className="w-44">
              <DateField id={controlId} aria-labelledby={labelId} {...(typed ? { 'aria-invalid': true, 'aria-describedby': errorId } as const : {})}
                value={typed ?? on} max={today} onChange={(v) => {
                  if (!ISO_DATE.test(v) || v > today) { setTyped(v); return; }
                  setTyped(null);
                  setSaved(null); setRestored(null); setUndone(false);
                  void navigate({ to: '/routine', replace: true, resetScroll: false,
                    search: (prev: RootSearch & RoutineSearch) => ({ ...prev, on: v === defaultRoutineDate(today) ? undefined : v }) });
                }} />
            </div>
          )}
        </FilterField>
      </FilterRow>
      {typed ? <p id={errorId} className="text-sm text-destructive">{t('routine.date_invalid')}</p> : null}
      {envelopes ? (
        <>
          {answer.error && !answer.data ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
          {!answer.data && !answer.error ? <div role="status" aria-busy="true" aria-label={t('routine.loading')} className="h-48 shimmer rounded-md border bg-muted" /> : null}
          {answer.data && answer.data.lines.length === 0 ? (
            <p className="flex flex-wrap items-center gap-2">
              {t('routine.none')}
              <Link to="/investments/new" className={buttonVariants({ variant: 'outline', size: 'sm' })}>{t('routine.create_envelope')}</Link>
            </p>
          ) : null}
          {answer.data && answer.data.lines.length > 0 && banked && !saved ? (
            <RoutineForm key={`${on}:${restored ? 'restored' : 'fresh'}:${answer.data.lines.map((l) => l.id).join(',')}`} view={answer.data} month={monthText}
              initial={startValues(answer.data, restored ?? loadDraft(on), banked)} banked={banked} onSaved={onSaved} />
          ) : null}
          {saved ? (
            <RoutineRecapPanel recap={saved.recap} onUndone={() => { setRestored(saved.values); setSaved(null); setUndone(true); }} />
          ) : null}
          {undone && !saved ? <p role="status" tabIndex={-1} ref={focusStatus}>{t('routine.undone')}</p> : null}
        </>
      ) : null}
      {modules.includes('realestate') ? <EstateSection key={on} on={on} /> : null}
      {modules.includes('childcare') ? <ChildcareSection on={on} /> : null}
    </div>
  );
}
