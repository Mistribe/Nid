import { useInfiniteQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import { ProblemError, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';

export type NotificationView = components['schemas']['NotificationView'];
export type NotificationPage = components['schemas']['NotificationPage'];
export type NotificationCount = components['schemas']['NotificationCount'];
export type SnoozeBody = components['schemas']['SnoozeBody'];

export const NOTIFICATIONS = '/api/v1/platform/notifications' as const;
export const NOTIFICATION_COUNT = '/api/v1/platform/notifications/count' as const;
export const READ_ALL_NOTIFICATIONS = '/api/v1/platform/notifications/read-all' as const;
export const READ_NOTIFICATION = '/api/v1/platform/notifications/{id}/read' as const;
export const DISMISS_NOTIFICATION = '/api/v1/platform/notifications/{id}/dismiss' as const;
export const SNOOZE_NOTIFICATION = '/api/v1/platform/notifications/{id}/snooze' as const;

export type NotificationList = 'todo' | 'snoozed' | 'history';

/** Every write changes the lists and the counts the bell shows. */
export function invalidateNotifications(queryClient: QueryClient): Promise<void> {
  return Promise.all([NOTIFICATIONS, NOTIFICATION_COUNT].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

/** The bell and its popover share this query; it follows the window focus and refreshes every 5 minutes. */
export function useNotificationCount() {
  const { $api } = useApi();
  return $api.useQuery('get', NOTIFICATION_COUNT, undefined, { refetchOnWindowFocus: true, refetchInterval: 300_000 });
}

export const PAGE = 50;

/** One list, page after page: `next` is the cursor of the following page; an empty page ends the list. */
export function useNotifications(list: NotificationList, limit: number = PAGE) {
  const { fetchClient } = useApi();
  return useInfiniteQuery({
    // Under the get/path prefix every write invalidates.
    queryKey: ['get', NOTIFICATIONS, { list, limit }],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam, signal }): Promise<NotificationPage> => {
      const { data, error } = await fetchClient.GET(NOTIFICATIONS, { params: { query: { list, limit, before: pageParam } }, signal });
      if (error) throw new ProblemError(toProblem(error));
      return data;
    },
    getNextPageParam: (last) => (last.entries.length > 0 ? (last.next ?? undefined) : undefined),
  });
}

/** The latest few, for the bell. */
export function useLatestNotifications(list: NotificationList | 'critical', limit: number, enabled = true) {
  const { $api } = useApi();
  return $api.useQuery('get', NOTIFICATIONS, { params: { query: { list, limit } } }, { enabled });
}

function useWrite() {
  const queryClient = useQueryClient();
  return { onSuccess: () => invalidateNotifications(queryClient) };
}

export function useReadAll() {
  const { $api } = useApi();
  return $api.useMutation('post', READ_ALL_NOTIFICATIONS, useWrite());
}

export function useRead() {
  const { $api } = useApi();
  return $api.useMutation('post', READ_NOTIFICATION, useWrite());
}

export function useDismiss() {
  const { $api } = useApi();
  return $api.useMutation('post', DISMISS_NOTIFICATION, useWrite());
}

export function useSnooze() {
  const { $api } = useApi();
  return $api.useMutation('post', SNOOZE_NOTIFICATION, useWrite());
}
