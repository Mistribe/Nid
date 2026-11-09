import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
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
  ])('%s invalidates the points and the catalogue', async (_name, invalidate) => {
    const queryClient = new QueryClient();
    const spy = vi.spyOn(queryClient, 'invalidateQueries');
    await invalidate(queryClient);
    const keys = spy.mock.calls.map(([filter]) => (filter?.queryKey as string[])[1]);
    expect(keys).toContain(SERIES_POINTS);
    expect(keys).toContain(SERIES_CATALOGUE);
  });
});
