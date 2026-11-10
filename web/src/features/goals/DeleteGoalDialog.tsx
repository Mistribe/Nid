import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { toProblem } from '@/api/errors';
import { invalidateGoals, useDeleteGoal, useGoalDeletion } from '@/api/goals';
import { DeleteItemDialog } from '@/components/DeleteItemDialog';
import '@/locales/deletion';
import '@/locales/goals';

/** Deleting a goal created by mistake: the shared dialog over the goal's preview; the list is where the household lands. */
export default function DeleteGoalDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const preview = useGoalDeletion(id, true);
  const remove = useDeleteGoal();
  const confirm = (version: number) => remove.mutate({ params: { path: { id }, query: { version } } }, {
    onSuccess: () => {
      toast.success(t('deletion.done', { name: preview.data?.name ?? '' }));
      void navigate({ to: '/goals' }).then(() => invalidateGoals(queryClient));
    },
    // The goal changed since the preview: read it again, the name is typed again.
    onError: (error) => { if (toProblem(error).status === 409) void preview.refetch(); },
  });
  return <DeleteItemDialog preview={preview.data} previewError={preview.error} closeHint={t('goals_list.close')} pending={remove.isPending}
    error={remove.error} onConfirm={confirm} onClose={onClose} />;
}
