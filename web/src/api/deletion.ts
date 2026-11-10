import { useApi } from './ApiProvider';
import type { paths } from './schema';

/** The routes that define an operation: openapi-typescript types an absent one as `?: never`. */
type WithOperation<M extends 'get' | 'delete'> = { [P in keyof paths]: paths[P][M] extends undefined ? never : P }[keyof paths];

/** One entry per item that can be deleted when created by mistake (#23): its preview and delete routes, and the list to land on. The « end it instead » hint is the close_hint entry of fr.deletion.json for the kind, only for the closable kinds. */
export const DELETABLE = {
  'goals.delete_goal': { preview: '/api/v1/goals/{id}/deletion', remove: '/api/v1/goals/{id}', list: '/goals', closable: true },
  'investments.delete_envelope': { preview: '/api/v1/investments/envelopes/{id}/deletion', remove: '/api/v1/investments/envelopes/{id}', list: '/investments', closable: true },
  'income.delete_income_source': { preview: '/api/v1/income/sources/{id}/deletion', remove: '/api/v1/income/sources/{id}', list: '/income', closable: true },
  'realestate.delete_property': { preview: '/api/v1/realestate/properties/{id}/deletion', remove: '/api/v1/realestate/properties/{id}', list: '/properties' },
  'realestate.delete_loan': { preview: '/api/v1/realestate/loans/{id}/deletion', remove: '/api/v1/realestate/loans/{id}', list: '/loans' },
  'subscriptions.delete_subscription': { preview: '/api/v1/subscriptions/{id}/deletion', remove: '/api/v1/subscriptions/{id}', list: '/subscriptions' },
} as const satisfies Record<string, { preview: WithOperation<'get'>; remove: WithOperation<'delete'>; list: string; closable?: true }>;
export type DeletableKind = keyof typeof DELETABLE;
/** The kinds that have an « end it instead » action, hence a close_hint string in fr.deletion.json. */
export type ClosableKind = { [K in DeletableKind]: (typeof DELETABLE)[K] extends { closable: true } ? K : never }[DeletableKind];
export const isClosable = (kind: DeletableKind): kind is ClosableKind => 'closable' in DELETABLE[kind];

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
