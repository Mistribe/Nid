import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import { SERIES_CATALOGUE, SERIES_POINTS } from '@/api/series';
import type { components } from '@/api/schema';
import { CHILDCARE } from '@/api/childcare';

export { CHILDCARE, useChildcareList, type ChildcareList, type ChildcareSummary, type Scope } from '@/api/childcare';
export type ChildcareDetail = components['schemas']['ChildcareDetail'];
export type ChildcareTerms = components['schemas']['ChildcareTermsView'];
export type WeekdaySlot = components['schemas']['WeekdaySlotView'];
export type CareGap = components['schemas']['CareGapView'];
export type CreateChildcareBody = components['schemas']['CreateChildcareBody'];
export type EditChildcareBody = components['schemas']['EditChildcareBody'];
export type TermsBody = components['schemas']['TermsBody'];
export type CareGapBody = components['schemas']['CareGapBody'];
export type StatementList = components['schemas']['ChildcareStatementList'];
export type StatementRow = components['schemas']['ChildcareStatementRow'];
export type Statement = components['schemas']['ChildcareStatementView'];
export type StatementDay = components['schemas']['StatementDayView'];
export type StatementDayBody = components['schemas']['StatementDayBody'];
export type Declaration = components['schemas']['DeclarationView'];
export type ChildcareYear = components['schemas']['ChildcareYearView'];

export const ARRANGEMENT = '/api/v1/childcare/{id}' as const;
export const REVISIONS = '/api/v1/childcare/{id}/revisions' as const;
export const GAPS = '/api/v1/childcare/{id}/gaps' as const;
export const WEEK_OVERRIDE = '/api/v1/childcare/{id}/week-overrides/{monday}' as const;
export const CLOSE = '/api/v1/childcare/{id}/close' as const;
export const REOPEN = '/api/v1/childcare/{id}/reopen' as const;
export const STATEMENTS = '/api/v1/childcare/{id}/statements' as const;
export const STATEMENT = '/api/v1/childcare/{id}/statements/{month}' as const;
export const VALIDATE = '/api/v1/childcare/{id}/statements/{month}/validate' as const;
export const REOPEN_STATEMENT = '/api/v1/childcare/{id}/statements/{month}/reopen' as const;
export const SUMMARY = '/api/v1/childcare/{id}/summary' as const;

/** Every write invalidates the list and every arrangement. */
export function invalidateChildcare(queryClient: QueryClient): Promise<void> {
  return Promise.all([CHILDCARE, ARRANGEMENT, SERIES_POINTS, SERIES_CATALOGUE].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

export function useChildcare(id: string) {
  const { $api } = useApi();
  return $api.useQuery('get', ARRANGEMENT, { params: { path: { id } } });
}

function useWrite() {
  const queryClient = useQueryClient();
  return { onSuccess: () => invalidateChildcare(queryClient) };
}

export function useCreateChildcare() {
  const { $api } = useApi();
  return $api.useMutation('post', CHILDCARE, useWrite());
}

export function useEditChildcare() {
  const { $api } = useApi();
  return $api.useMutation('put', ARRANGEMENT, useWrite());
}

export function useReviseChildcare() {
  const { $api } = useApi();
  return $api.useMutation('post', REVISIONS, useWrite());
}

export function useReplaceGaps() {
  const { $api } = useApi();
  return $api.useMutation('put', GAPS, useWrite());
}

export function useOverrideWeek() {
  const { $api } = useApi();
  return $api.useMutation('put', WEEK_OVERRIDE, useWrite());
}

export function useCloseChildcare() {
  const { $api } = useApi();
  return $api.useMutation('post', CLOSE, useWrite());
}

export function useReopenChildcare() {
  const { $api } = useApi();
  return $api.useMutation('post', REOPEN, useWrite());
}

export function useStatements(id: string) {
  const { $api } = useApi();
  return $api.useQuery('get', STATEMENTS, { params: { path: { id } } });
}

export function useStatement(id: string, month: string) {
  const { $api } = useApi();
  return $api.useQuery('get', STATEMENT, { params: { path: { id, month } } });
}

/** A statement write answers with the month as it now reads: the month and the list are refetched. */
export function invalidateStatements(queryClient: QueryClient): Promise<void> {
  return Promise.all([STATEMENTS, STATEMENT, SUMMARY, SERIES_POINTS].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

function useStatementWrite() {
  const queryClient = useQueryClient();
  return { onSuccess: () => invalidateStatements(queryClient) };
}

export function useSaveStatement() {
  const { $api } = useApi();
  return $api.useMutation('put', STATEMENT, useStatementWrite());
}

export function useValidateStatement() {
  const { $api } = useApi();
  return $api.useMutation('post', VALIDATE, useStatementWrite());
}

export function useReopenStatement() {
  const { $api } = useApi();
  return $api.useMutation('post', REOPEN_STATEMENT, useStatementWrite());
}

export function useDiscardStatement() {
  const { $api } = useApi();
  return $api.useMutation('delete', STATEMENT, useStatementWrite());
}

/** A day of the view as the body sends it back. */
export const dayBody = (d: StatementDay): StatementDayBody => ({
  date: d.date, present: d.present, arrival: d.arrival, departure: d.departure, lunch: d.lunch, snack: d.snack, absence: d.absence,
});
