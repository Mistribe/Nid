import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toProblem } from '@/api/errors';
import { DELETABLE, useDeleteItem, useItemDeletionPreview, type DeletableKind } from '@/api/deletion';
import { useApi } from '@/api/ApiProvider';
import { APPROVE, invalidateProposals } from '@/api/proposals';
import { DeleteItemDialog } from '@/components/DeleteItemDialog';
import { useItemDeleted } from '@/components/useItemDeleted';
import '@/locales/deletion';
import '@/locales/goals';
import '@/locales/income';

/**
 * Deleting an item created by mistake (#23): the shared dialog over the item's preview; the module's list is where the household lands.
 * With `approve` the confirm approves the proposal to delete it (with the preview's version) instead of calling the item's DELETE.
 */
export default function DeleteItemFlow({ kind, id, onClose, approve }: {
  kind: DeletableKind; id: string; onClose: () => void; approve?: { proposalId: string; proposalVersion: number };
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const { list, closeHint } = DELETABLE[kind];
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
  return <DeleteItemDialog preview={preview.data} previewError={preview.error} closeHint={t(closeHint)} pending={active.isPending}
    error={active.error} onConfirm={confirm} onClose={onClose} />;
}
