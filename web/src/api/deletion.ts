import { useApi } from './ApiProvider';
import { GOAL, GOAL_DELETION } from './goals';

/** One entry per item that can be deleted when created by mistake (#23): its preview and delete routes, the list to land on, and the « end it instead » hint. */
export const DELETABLE = {
  'goals.delete_goal': { preview: GOAL_DELETION, remove: GOAL, list: '/goals', closeHint: 'goals_list.close' },
  'income.delete_income_source': {
    preview: '/api/v1/income/sources/{id}/deletion', remove: '/api/v1/income/sources/{id}', list: '/income', closeHint: 'income_detail.ending.open',
  },
} as const;
export type DeletableKind = keyof typeof DELETABLE;

// Every kind shares the same shape (`{id}` path, `version` query, ItemDeletionPreview), so the routes are typed once, as the goal's.
type PreviewPath = typeof GOAL_DELETION;
type RemovePath = typeof GOAL;

/** What deleting the item removes and what blocks it, read only while the dialog is open. */
export function useItemDeletionPreview(kind: DeletableKind, id: string, enabled: boolean) {
  const { $api } = useApi();
  return $api.useQuery('get', DELETABLE[kind].preview as PreviewPath, { params: { path: { id } } }, { enabled });
}

/** No invalidation here: the item is gone, so the page leaves first and refreshes the lists after (a refetch of it would 404). */
export function useDeleteItem(kind: DeletableKind) {
  const { $api } = useApi();
  const remove = $api.useMutation('delete', DELETABLE[kind].remove as RemovePath);
  return {
    ...remove,
    mutate: ({ id, version }: { id: string; version: number }, options?: Parameters<typeof remove.mutate>[1]) =>
      remove.mutate({ params: { path: { id }, query: { version } } }, options),
  };
}
