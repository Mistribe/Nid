import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { invalidateChildcare, invalidateStatements } from '@/features/childcare/api';
import { useRefreshImports } from '@/features/import/queries';
import { invalidateAssessments } from '@/features/tax/assessments/api';
import { invalidateGoals } from './goals';
import { invalidateSubscriptions } from './subscriptions';
import { invalidateIncome } from './income';
import { invalidateInvestments } from './investments';
import { invalidateLoans } from './loans';
import { invalidateProperties } from './properties';
import { SERIES_CATALOGUE, SERIES_POINTS } from './series';

describe('the series explorer follows every write', () => {
  it.each([
    ['income', invalidateIncome],
    ['investments', invalidateInvestments],
    ['loans', invalidateLoans],
    ['properties', invalidateProperties],
    ['subscriptions', invalidateSubscriptions],
    ['goals', invalidateGoals],
    ['childcare', invalidateChildcare],
    ['tax notices', invalidateAssessments],
  ])('%s invalidates the points and the catalogue', async (_name, invalidate) => {
    const queryClient = new QueryClient();
    const spy = vi.spyOn(queryClient, 'invalidateQueries');
    await invalidate(queryClient);
    const keys = spy.mock.calls.map(([filter]) => (filter?.queryKey as string[])[1]);
    expect(keys).toContain(SERIES_POINTS);
    expect(keys).toContain(SERIES_CATALOGUE);
  });

  it('a statement write invalidates the points', async () => {
    const queryClient = new QueryClient();
    const spy = vi.spyOn(queryClient, 'invalidateQueries');
    await invalidateStatements(queryClient);
    expect(spy.mock.calls.map(([filter]) => (filter?.queryKey as string[])[1])).toContain(SERIES_POINTS);
  });

  it('an import refresh invalidates the points and the catalogue', () => {
    const queryClient = new QueryClient();
    const spy = vi.spyOn(queryClient, 'invalidateQueries');
    const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client: queryClient }, children);
    renderHook(() => useRefreshImports(), { wrapper }).result.current();
    const keys = spy.mock.calls.map(([filter]) => (filter?.queryKey as string[])[1]);
    expect(keys).toContain(SERIES_POINTS);
    expect(keys).toContain(SERIES_CATALOGUE);
  });
});
