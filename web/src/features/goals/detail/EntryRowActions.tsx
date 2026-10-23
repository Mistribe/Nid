import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { RowActions } from '@/components/page/RowActions';
import { invalidateGoals, PROGRESS, type GoalEntry } from '../api';
import { presentGoalConflict } from '../format';
import '@/locales/goals';

/** A reading's « Supprimer », its own mutation per row (as `OneOffRowActions`): a refusal shows on this row only. */
export function EntryRowActions({ goalId, entry, onDeleteStarted, onDeleted, onReloaded }: {
  goalId: string; entry: GoalEntry; onDeleteStarted: () => void; onDeleted: (message: string) => void; onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // On the mutation, not on `mutate`: the row is gone once the goal refetches.
  const remove = $api.useMutation('delete', PROGRESS, {
    onSuccess: () => { void invalidateGoals(queryClient); onDeleted(t('goals_detail.entries.deleted')); },
  });
  const date = formatLocalDate(entry.on);
  return (
    <RowActions menuId={`entry-menu-${entry.on}`} menuLabel={t('goals_detail.entries.menu', { date })} items={[]}
      confirmation={t('goals_detail.entries.delete_confirm', { date })} remove={remove} onDeleteStarted={onDeleteStarted}
      present={presentGoalConflict} invalidate={invalidateGoals} onReloaded={onReloaded}
      onConfirm={() => remove.mutate({ params: { path: { id: goalId, on: entry.on } } })} />
  );
}
