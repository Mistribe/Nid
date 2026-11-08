import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { DASHBOARD } from '@/api/dashboard';
import { invalidateInvestments } from '@/api/investments';
import { GOAL, GOALS, invalidateGoals } from '@/api/goals';

const invalidated = async (run: (q: QueryClient) => Promise<void>) => {
  const queryClient = new QueryClient();
  const spy = vi.spyOn(queryClient, 'invalidateQueries');
  await run(queryClient);
  return spy.mock.calls.map(([filters]) => filters?.queryKey?.[1]);
};

describe('goal queries', () => {
  it('an investments write refetches the goals, which read the envelopes', async () => {
    expect(await invalidated(invalidateInvestments)).toEqual(expect.arrayContaining([GOALS, GOAL]));
  });

  it('a goals write leaves the dashboard alone: its goals card reads the goals list', async () => {
    expect(await invalidated(invalidateGoals)).not.toContain(DASHBOARD);
  });
});
