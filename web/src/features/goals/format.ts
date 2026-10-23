import type { TFunction } from 'i18next';
import { present, toProblem, type Presentation } from '@/api/errors';
import '@/locales/goals';

export const statusText = (t: TFunction, status: string) => t(`goals_list.status.${status}`, { defaultValue: status });
export const kindText = (t: TFunction, kind: string) => t(`goals_list.kind.${kind}`, { defaultValue: kind });

/** The server's percentage in French, `digits` decimals at most (1 for a progress, 2 for a share); display only. */
export const pctText = (value: number, digits: 1 | 2) => new Intl.NumberFormat('fr-FR', { maximumFractionDigits: digits }).format(value);

const REFUSALS = {
  'goals.share_exceeded': 'goals_list.refused.share_exceeded',
  'goals.goal_closed': 'goals_list.refused.goal_closed',
  'goals.goal_open': 'goals_list.refused.goal_open',
} as const;

/** A refused close or reopen, as `presentConflict` (investments/entries/entries.ts): the goal's own sentence for the codes it knows. */
export function presentGoalConflict(t: TFunction, error: unknown): Presentation {
  const problem = toProblem(error);
  const base = present(problem);
  const code = problem.code ?? '';
  if (base.kind !== 'conflict' || !Object.hasOwn(REFUSALS, code)) return base;
  return { ...base, detail: t(REFUSALS[code as keyof typeof REFUSALS]) };
}
