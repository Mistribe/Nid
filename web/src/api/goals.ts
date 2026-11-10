import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useApi } from './ApiProvider';
import type { components } from './schema';
import { SERIES_CATALOGUE, SERIES_POINTS } from './series';

export type GoalSummary = components['schemas']['GoalSummary'];
export type GoalList = components['schemas']['GoalList'];
export type GoalDetail = components['schemas']['GoalDetail'];
export type GoalFunding = components['schemas']['GoalFundingView'];
export type GoalEntry = components['schemas']['GoalEntryView'];
export type GoalCommitment = components['schemas']['GoalCommitmentView'];
export type GoalMoney = components['schemas']['GoalMoneyView'];
export type CreateGoalBody = components['schemas']['CreateGoalBody'];
export type EditGoalBody = components['schemas']['EditGoalBody'];
export type GoalFundingBody = components['schemas']['GoalFundingBody'];
export type EmergencyTarget = components['schemas']['EmergencyTargetView'];

export const GOALS = '/api/v1/goals' as const;
export const GOAL = '/api/v1/goals/{id}' as const;
export const CLOSE_GOAL = '/api/v1/goals/{id}/close' as const;
export const GOAL_DELETION = '/api/v1/goals/{id}/deletion' as const;
export const REOPEN_GOAL = '/api/v1/goals/{id}/reopen' as const;
export const PROGRESS = '/api/v1/goals/{id}/progress/{on}' as const;
export const EMERGENCY_TARGET = '/api/v1/goals/emergency-target' as const;

export type Scope = 'me' | 'household';

/** Every write invalidates the list (the dashboard's goals card reads it too) and every goal. */
export function invalidateGoals(queryClient: QueryClient): Promise<void> {
  return Promise.all([GOALS, GOAL, SERIES_POINTS, SERIES_CATALOGUE].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

/** `closed` lists the open goals and the closed ones; the page keeps the rows it needs. */
export function useGoals(scope: Scope, closed: boolean) {
  const { $api } = useApi();
  return $api.useQuery('get', GOALS, { params: { query: { scope, closed } } });
}


export function useGoal(id: string, scope: Scope) {
  const { $api } = useApi();
  return $api.useQuery('get', GOAL, { params: { path: { id }, query: { scope } } });
}

/** Issue #412: the target an emergency fund of `months` would have, computed by the server before the goal is saved. */
export function useEmergencyTarget(months: number | null, scope: Scope) {
  const { $api } = useApi();
  return $api.useQuery('get', EMERGENCY_TARGET, { params: { query: { months: months ?? 0, scope } } }, { enabled: months !== null });
}

function useWrite() {
  const queryClient = useQueryClient();
  return { onSuccess: () => invalidateGoals(queryClient) };
}

export function useCreateGoal() {
  const { $api } = useApi();
  return $api.useMutation('post', GOALS, useWrite());
}

export function useEditGoal() {
  const { $api } = useApi();
  return $api.useMutation('put', GOAL, useWrite());
}

export function useCloseGoal() {
  const { $api } = useApi();
  return $api.useMutation('post', CLOSE_GOAL, useWrite());
}

export function useReopenGoal() {
  const { $api } = useApi();
  return $api.useMutation('post', REOPEN_GOAL, useWrite());
}

export function usePutProgress() {
  const { $api } = useApi();
  return $api.useMutation('put', PROGRESS, useWrite());
}
