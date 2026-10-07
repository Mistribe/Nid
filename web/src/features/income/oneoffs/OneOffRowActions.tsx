import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ConfirmAction } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentConflict } from '../../investments/entries/entries';
import { Reload } from '../../subscriptions/edits/Reload';
import { invalidateIncome, ONE_OFF, type IncomeOneOff, type IncomeSourceDetail } from '../api';

/** « Corriger » swaps the row for its form; « Supprimer » asks first. The confirmation goes to the section's live region. */
export function OneOffRowActions({ source, oneOff, onEdit, onDeleteStarted, onDeleted }: {
  source: IncomeSourceDetail; oneOff: IncomeOneOff; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', ONE_OFF, {
    onSuccess: () => { void invalidateIncome(queryClient); onDeleted(t('income_detail.one_offs.deleted')); },
  });
  const problem = remove.error ? presentConflict(t, remove.error) : null;

  return (
    <span className="flex flex-wrap items-center gap-2">
      <Button id={`correct-one-off-${oneOff.id}`} type="button" variant="outline" size="sm" onClick={onEdit}>{t('income_detail.correct')}</Button>
      <ConfirmAction label={t('income_detail.delete')} confirmation={t('income_detail.one_offs.delete_confirm')} pending={remove.isPending}
        onConfirm={() => { onDeleteStarted(); remove.mutate({ params: { path: { id: source.id, flowId: oneOff.id } } }); }} />
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => { remove.reset(); return invalidateIncome(queryClient); }} /> : null}
        </>
      ) : null}
    </span>
  );
}
