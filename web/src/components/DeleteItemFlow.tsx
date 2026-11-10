import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toProblem } from '@/api/errors';
import { DELETABLE, useDeleteItem, useItemDeletionPreview, type DeletableKind } from '@/api/deletion';
import { useApi } from '@/api/ApiProvider';
import { APPROVE, invalidateProposals } from '@/api/proposals';
import { DeleteItemDialog } from '@/components/DeleteItemDialog';
import { useItemDeleted } from '@/components/useItemDeleted';
import '@/locales/deletion';

/**
 * Deleting an item created by mistake (#23): the shared dialog over the item's preview; the module's list is where the household lands.
 * `closed`: the item is already ended, so no « end it instead » hint (nor for a kind without a close hint). With `approve` the confirm approves the proposal to delete it (with the preview's version) instead of calling the item's DELETE.
 */
export default function DeleteItemFlow({ kind, id, onClose, approve, closed }: {
  kind: DeletableKind; id: string; onClose: () => void; closed?: boolean; approve?: { proposalId: string; proposalVersion: number };
}) {
  const { t, i18n } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const { list } = DELETABLE[kind];
  // Typed as a key of the strings: the real-estate kinds have none, so the lookup is checked at run time.
  const hintKey = `deletion.close_hint.${kind}` as 'deletion.close_hint.goals.delete_goal';
  const deleted = useItemDeleted(list);
  const preview = useItemDeletionPreview(kind, id, true);
  const remove = useDeleteItem(kind);
  const approval = $api.useMutation('post', APPROVE);
  const outcome = {
    onSuccess: () => deleted(preview.data?.name ?? ''),
    onError: (error: unknown) => {
      // The item changed since the preview: read it again, the name is typed again.
      if (toProblem(error).status === 409) void preview.refetch();
      // A refused approval writes its warning on the proposal (a new version): the next confirm must send that one.
      if (approve) void invalidateProposals(queryClient);
    },
  };
  const confirm = (version: number) => approve
    ? approval.mutate({ params: { path: { id: approve.proposalId } }, body: { version: approve.proposalVersion, editedPayload: { id, version } } }, outcome)
    : remove.mutate({ id, version }, outcome);
  const active = approve ? approval : remove;
  return <DeleteItemDialog preview={preview.data} previewError={preview.error} closeHint={closed || !i18n.exists(hintKey) ? undefined : t(hintKey) as string} pending={active.isPending}
    error={active.error} onConfirm={confirm} onClose={onClose} />;
}
