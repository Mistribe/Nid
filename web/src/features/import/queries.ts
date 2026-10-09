import { useQueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import { DASHBOARD, NET_WORTH } from '../dashboard/api';

/**
 * Marks the import list and every session as stale after a step changed one on the server (start, cancel,
 * validate, commit), and the dashboard, since a commit writes: with a 30 s `staleTime`, a page coming back would otherwise show the state it read before.
 * Mounted queries refetch at once; the others on their next mount.
 */
export function useRefreshImports(): () => void {
  const queryClient = useQueryClient();
  return useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['get', '/api/v1/platform/imports'] });
    void queryClient.invalidateQueries({ queryKey: ['get', '/api/v1/platform/imports/{id}'] });
    void queryClient.invalidateQueries({ queryKey: ['get', DASHBOARD] });
  void queryClient.invalidateQueries({ queryKey: ['get', NET_WORTH] });
  }, [queryClient]);
}
