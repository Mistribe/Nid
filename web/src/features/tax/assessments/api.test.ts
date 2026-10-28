import { QueryClient } from '@tanstack/react-query';
import { expect, it, vi } from 'vitest';
import { invalidateAssessments } from './api';

it('a notice write marks stale every read it feeds: the estimate, the 2044 and the dashboard', async () => {
  const queryClient = new QueryClient();
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

  await invalidateAssessments(queryClient);

  expect(invalidate.mock.calls.map(([filters]) => filters?.queryKey)).toEqual(expect.arrayContaining([
    ['get', '/api/v1/tax/assessments'], ['get', '/api/v1/tax'], ['get', '/api/v1/tax/2044'], ['get', '/api/v1/dashboard'],
  ]));
});
