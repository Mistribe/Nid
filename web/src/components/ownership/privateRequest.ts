import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { invalidateProposals, PROPOSALS } from '@/api/proposals';
import { ITEM_SHARING, useItemSharing } from './SharingBadges';
import type { OwnedDetail, OwnershipValues } from './ownership';
import '@/locales/forms';

/**
 * #384: the sole owner of a shared item makes it private while the other adult reads it. The other adult must agree: the change goes
 * to them as a request in « À valider », and the item stays shared until they decide. `canAsk` comes from the server.
 */
export function usePrivateRequest(item: string, enabled = true) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const canAsk = useItemSharing(item, enabled).data?.canAskPrivate === true;
  const send = $api.useMutation('post', PROPOSALS, {
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['get', ITEM_SHARING] });
      void invalidateProposals(queryClient);
    },
  });
  return {
    canAsk,
    /** What a save sends, and whether it asks: private chosen, the owner unchanged, the item kept shared in the save itself. */
    split<V extends Partial<OwnershipValues>>(before: OwnedDetail, values: V): [V, boolean] {
      const asks = canAsk && values.visibility === 'private' && values.owners?.length === 1
        && values.owners[0]?.personId === before.owners[0]?.personId;
      return asks ? [{ ...values, visibility: 'shared' }, true] : [values, false];
    },
    /** Once the rest is saved (or there was nothing else): sends the request when the save asks, then tells `onDone`. */
    settle(asks: boolean, message: string, onDone: (message: string) => void, onError: (error: unknown) => void) {
      if (!asks) { onDone(message); return; }
      send.mutate({ body: { kind: 'core.make_private', targetItemId: item, payload: {} } },
        { onSuccess: () => onDone(t('sharing.private_asked')), onError });
    },
  };
}
