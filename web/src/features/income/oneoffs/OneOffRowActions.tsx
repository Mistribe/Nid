import { useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { invalidateIncome, ONE_OFF, type IncomeOneOff, type IncomeSourceDetail } from '../api';
import { RowActions } from '@/components/page/RowActions';
import { presentConflict } from '../../investments/entries/entries';
import { oneOffTitle } from '../payslips/payslip';
import { FileInput } from '../payslips/PayslipCell';
import type { PayslipWrites } from '../payslips/usePayslipWrites';
import '@/locales/income';

/** « Corriger » opens the row's dialog; « Supprimer » asks first. The confirmation goes to the section's live region; after « Recharger » the row may be gone, so the section takes the focus. */
export function OneOffRowActions({ source, oneOff, writes, onEdit, onDeleteStarted, onDeleted, onReloaded }: {
  source: IncomeSourceDetail; oneOff: IncomeOneOff; writes: PayslipWrites; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void;
  onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', ONE_OFF, {
    onSuccess: () => { void invalidateIncome(queryClient); onDeleted(t('income_detail.one_offs.deleted')); },
  });
  const input = useRef<HTMLInputElement>(null);
  return (
    <>
    <RowActions menuId={`one-off-menu-${oneOff.id}`} menuLabel={t('income_detail.one_offs.menu', { date: formatLocalDate(oneOff.occurredOn), amount: formatMoney(oneOff.amount) })}
      items={[
        { label: t('income_detail.correct'), onSelect: onEdit },
        { label: t('income_detail.payslips.attach_one_off'), onSelect: () => input.current?.click() },
      ]}
      confirmation={t('income_detail.one_offs.delete_confirm')} remove={remove} onDeleteStarted={onDeleteStarted} present={presentConflict} invalidate={invalidateIncome} onReloaded={onReloaded}
      onConfirm={() => remove.mutate({ params: { path: { id: source.id, flowId: oneOff.id } } })} />
    <FileInput ref={input} label={t('income_detail.payslips.attach_one_off')}
      onFile={(file) => void writes.attach(file, { kind: 'income.one_off', id: oneOff.id }, oneOff.occurredOn, oneOffTitle(t, oneOff))} />
    </>
  );
}
