import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import type { UseFormSetError } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toProblem, type Problem } from '@/api/errors';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { invalidateGoals } from './api';
import { pctText } from './format';
import { goalPath, type GoalValues } from './goalSchema';
import '@/locales/goals';

/** `freeBp` rides on the 409 as an extension member of the Problem. */
type ShareExceeded = Problem & { freeBp?: number };

/**
 * A refused write, where it belongs: a 422 on its field, `share_exceeded` as the free share on the funding row, a stale
 * version as « Cet objectif a changé entre-temps » (and the goal refetched); any other refusal as one message.
 */
export function useGoalErrors(setError: UseFormSetError<GoalValues>) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const base = useSubmitErrors(setError, goalPath, (f) => t(`goals_form.error.${f.code.replaceAll('.', '_')}`, { defaultValue: f.message }));
  const [stale, setStale] = useState(false);

  return {
    problem: stale ? null : base.problem,
    stale,
    clear: () => { base.clear(); setStale(false); },
    onError(error: unknown) {
      const problem = toProblem(error) as ShareExceeded;
      setStale(problem.code === 'goals.conflict');
      if (problem.code === 'goals.conflict') { void invalidateGoals(queryClient); return; }
      if (problem.code === 'goals.share_exceeded' && problem.freeBp !== undefined) {
        // The 409 names the row in `errors[]` (`funding[i].shareBp`); the first row only when it does not.
        const named = problem.errors?.map((f) => goalPath(f.path)).find((n) => n?.startsWith('funding.'));
        setError(named ?? 'funding.0.share', { type: 'server', message: t('goals_form.free_share', { pct: pctText(problem.freeBp / 100, 2) }) });
        return;
      }
      base.onError(error);
    },
  };
}
