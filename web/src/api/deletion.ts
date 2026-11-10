import { useApi } from './ApiProvider';
import type { paths } from './schema';

/** The routes that define an operation: openapi-typescript types an absent one as `?: never`. */
type WithOperation<M extends 'get' | 'delete'> = { [P in keyof paths]: paths[P][M] extends undefined ? never : P }[keyof paths];

/** One entry per item that can be deleted when created by mistake (#23): its preview and delete routes, and the list to land on. The « end it instead » hint is `deletion.close_hint.<kind>`. */
export const DELETABLE = {
  'goals.delete_goal': { preview: '/api/v1/goals/{id}/deletion', remove: '/api/v1/goals/{id}', list: '/goals' },
  'income.delete_income_source': { preview: '/api/v1/income/sources/{id}/deletion', remove: '/api/v1/income/sources/{id}', list: '/income' },
} as const satisfies Record<string, { preview: WithOperation<'get'>; remove: WithOperation<'delete'>; list: string }>;
export type DeletableKind = keyof typeof DELETABLE;

// Every kind shares the same shape (`{id}` path, `version` query, ItemDeletionPreview), so the hooks type the routes once, as the goal's:
// the `satisfies` above is what checks a new entry's paths against the API.
type PreviewPath = '/api/v1/goals/{id}/deletion';
type RemovePath = '/api/v1/goals/{id}';

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
