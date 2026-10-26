import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import { ProblemError, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';

export type PreferencesView = components['schemas']['PreferencesView'];
export type LeadDayView = components['schemas']['LeadDayView'];
export type NotificationPreferencesBody = components['schemas']['NotificationPreferencesBody'];
export type TestChannelResult = components['schemas']['TestChannelResult'];

export const NOTIFICATION_PREFERENCES = '/api/v1/platform/notifications/preferences' as const;
export const NOTIFICATION_TEST = '/api/v1/platform/notifications/test' as const;

export function usePreferences() {
  const { $api } = useApi();
  return $api.useQuery('get', NOTIFICATION_PREFERENCES);
}

/** The refetch « Recharger » runs. */
export function useReloadPreferences() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: ['get', NOTIFICATION_PREFERENCES] });
}

/** Replaces the whole set; the saved view takes the place of the cached one. */
export function useSavePreferences() {
  const { $api, fetchClient } = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: NotificationPreferencesBody): Promise<PreferencesView> => {
      const { data, error } = await fetchClient.PUT(NOTIFICATION_PREFERENCES, { body });
      if (error) throw new ProblemError(toProblem(error));
      return data;
    },
    onSuccess: (view) => {
      queryClient.setQueryData($api.queryOptions('get', NOTIFICATION_PREFERENCES).queryKey, view);
    },
  });
}

export function useTestChannel() {
  const { $api } = useApi();
  return $api.useMutation('post', NOTIFICATION_TEST);
}
