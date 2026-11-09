import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import type { components } from '@/api/schema';
import { invalidateDocuments } from '@/api/documents';
import { DASHBOARD } from '@/api/dashboard';
import { SERIES_CATALOGUE, SERIES_POINTS } from '@/api/series';
import { TAX, TAX_2044 } from '@/api/tax';

export type TaxAssessment = components['schemas']['TaxAssessmentView'];
export type TaxAssessmentBody = components['schemas']['AddTaxAssessmentBody'];

export const TAX_ASSESSMENTS = '/api/v1/tax/assessments' as const;
export const TAX_ASSESSMENT = '/api/v1/tax/assessments/{id}' as const;
export const SALARY_ALLOWANCE = '/api/v1/tax/assessments/salary-allowance' as const;
export const TAX_NOTICE_PREVIEW = '/api/v1/documents/{id}/tax-notice-preview' as const;

export function useTaxAssessments(scope: 'me' | 'household', year?: number) {
  const { $api } = useApi();
  return $api.useQuery('get', TAX_ASSESSMENTS, { params: { query: { scope, year } } });
}

export function useTaxAssessment(id: string) {
  const { $api } = useApi();
  return $api.useQuery('get', TAX_ASSESSMENT, { params: { path: { id } } });
}

/** Every write invalidates the notices, the estimates, the 2044 and the dashboard's PER deadline (a notice feeds them) and the documents (the PDF's links). */
export function invalidateAssessments(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    // The list is refetched even when no page shows it: the Tax page remounts on it right after the save and must not read the old one.
    queryClient.invalidateQueries({ queryKey: ['get', TAX_ASSESSMENTS], refetchType: 'all' }),
    ...[TAX_ASSESSMENT, TAX, TAX_2044, DASHBOARD, SERIES_POINTS, SERIES_CATALOGUE].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] })),
    invalidateDocuments(queryClient),
  ]).then(() => undefined);
}

function useWrite() {
  const queryClient = useQueryClient();
  return { onSuccess: () => invalidateAssessments(queryClient) };
}

export function useAddTaxAssessment() {
  const { $api } = useApi();
  return $api.useMutation('post', TAX_ASSESSMENTS, useWrite());
}

export function useCorrectTaxAssessment() {
  const { $api } = useApi();
  return $api.useMutation('put', TAX_ASSESSMENT, useWrite());
}

export function useDeleteTaxAssessment() {
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The deleted notice's detail leaves the cache: refetching it would answer 404 to a page still showing it (the correction form).
  return $api.useMutation('delete', TAX_ASSESSMENT, {
    onSuccess: () => { queryClient.removeQueries({ queryKey: ['get', TAX_ASSESSMENT] }); return invalidateAssessments(queryClient); },
  });
}
