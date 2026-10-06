import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ConfirmAction } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentConflict } from '../../investments/entries/entries';
import { Reload } from '../../subscriptions/edits/Reload';
import { invalidateIncome, PHASE, type IncomePhase, type IncomeSourceDetail } from '../api';

/**
 * A phase's row actions (spec §7): « Corriger » swaps the row for its form; « Supprimer » asks first, then deletes with
 * the version it read. The confirmation goes to the page's live region: the row disappears with the refetch.
 */
export function PhaseRowActions({ source, phase, onEdit, onDeleteStarted, onDeleted }: {
  source: IncomeSourceDetail; phase: IncomePhase; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', PHASE, {
    onSuccess: () => { void invalidateIncome(queryClient); onDeleted(t('income_detail.phase_deleted')); },
  });
  const problem = remove.error ? presentConflict(t, remove.error) : null;

  return (
    <span className="flex flex-wrap items-center gap-2">
      <Button id={`correct-phase-${phase.id}`} type="button" variant="outline" size="sm" onClick={onEdit}>{t('income_detail.correct')}</Button>
      <ConfirmAction label={t('income_detail.delete')} confirmation={t('income_detail.delete_confirm')} pending={remove.isPending}
        onConfirm={() => {
          onDeleteStarted();
          remove.mutate({ params: { path: { id: source.id, phaseId: phase.id }, query: { version: phase.version } } });
        }} />
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => { remove.reset(); return invalidateIncome(queryClient); }} /> : null}
        </>
      ) : null}
    </span>
  );
}
