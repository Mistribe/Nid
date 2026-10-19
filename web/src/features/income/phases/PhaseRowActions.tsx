import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { invalidateIncome, PHASE, type IncomePhase, type IncomeSourceDetail } from '../api';
import { RowActions } from '@/components/page/RowActions';
import { presentConflict } from '../../investments/entries/entries';

/**
 * A phase's `⋯` menu (spec §7): « Changer le montant » on a running or upcoming phase, « Corriger », and « Supprimer »,
 * which asks first and deletes with the version it read. The confirmation goes to the page's live region: the row
 * disappears with the refetch.
 */
export function PhaseRowActions({ source, phase, onEdit, onChangeAmount, onDeleteStarted, onDeleted }: {
  source: IncomeSourceDetail; phase: IncomePhase; onEdit: () => void; onChangeAmount?: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', PHASE, {
    onSuccess: () => { void invalidateIncome(queryClient); onDeleted(t('income_detail.phase_deleted')); },
  });
  return (
    <RowActions menuId={`phase-menu-${phase.id}`} menuLabel={t('income_detail.phase_menu', { label: phase.label, date: formatLocalDate(phase.startsOn) })}
      items={[
        ...(onChangeAmount ? [{ label: t('income_detail.change.open'), onSelect: onChangeAmount }] : []),
        { label: t('income_detail.correct'), onSelect: onEdit },
      ]}
      confirmation={t('income_detail.delete_confirm')} remove={remove} onDeleteStarted={onDeleteStarted} present={presentConflict} invalidate={invalidateIncome}
      onConfirm={() => remove.mutate({ params: { path: { id: source.id, phaseId: phase.id }, query: { version: phase.version } } })} />
  );
}
