import { useTranslation } from 'react-i18next';
import { toProblem } from '@/api/errors';
import { useDeleteGoal, useGoalDeletion } from '@/api/goals';
import { DeleteItemDialog } from '@/components/DeleteItemDialog';
import { useItemDeleted } from '@/components/useItemDeleted';
import '@/locales/deletion';
import '@/locales/goals';

/** Deleting a goal created by mistake: the shared dialog over the goal's preview; the list is where the household lands. */
export default function DeleteGoalDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation();
  const deleted = useItemDeleted('/goals');
  const preview = useGoalDeletion(id, true);
  const remove = useDeleteGoal();
  const confirm = (version: number) => remove.mutate({ params: { path: { id }, query: { version } } }, {
    onSuccess: () => deleted(preview.data?.name ?? ''),
    // The goal changed since the preview: read it again, the name is typed again.
    onError: (error) => { if (toProblem(error).status === 409) void preview.refetch(); },
  });
  return <DeleteItemDialog preview={preview.data} previewError={preview.error} closeHint={t('goals_list.close')} pending={remove.isPending}
    error={remove.error} onConfirm={confirm} onClose={onClose} />;
}
