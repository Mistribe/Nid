import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { RowActions as SharedRowActions } from '@/components/page/RowActions';
import { FLOW, invalidateInvestments, VALUATION, type EnvelopeDetail, type Flow, type Valuation } from '@/api/investments';
import { flowKindText, isTransferLeg } from '../format';
import { presentConflict } from '@/components/form/fieldErrors';
import '@/locales/investments';

type RowActionsProps = {
  envelope: EnvelopeDetail;
  onEdit: () => void;
  /** The deletion's confirmation message: the row (and this very component) disappears once the refetch lands, so
   * the toast and the focus it moves belong to the page. */
  onDeleted: (message: string) => void;
} & ({ kind: 'value'; item: Valuation } | { kind: 'flow'; item: Flow });

/**
 * A value's or a flow's row menu (2B2a design §6): « Corriger » opens its dialog; « Supprimer » asks first, then
 * deletes. A transfer leg (`category = transfer`, 2B2b owns transfers) cannot be corrected here — only deleted, with
 * a warning that its other leg keeps its own.
 */
export function RowActions(props: RowActionsProps) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const transferLeg = props.kind === 'flow' && isTransferLeg(props.item);

  const removeValuation = $api.useMutation('delete', VALUATION, {
    onSuccess: () => { void invalidateInvestments(queryClient); props.onDeleted(t('envelope.value_deleted')); },
  });
  const removeFlow = $api.useMutation('delete', FLOW, {
    onSuccess: () => { void invalidateInvestments(queryClient); props.onDeleted(t('envelope.flow_deleted')); },
  });
  const remove = props.kind === 'value' ? removeValuation : removeFlow;

  const confirmation = props.kind === 'value'
    ? t('envelope.delete_value')
    : transferLeg ? t('envelope.delete_transfer_leg') : t('envelope.delete_flow');

  const onConfirm = () => {
    if (props.kind === 'value') removeValuation.mutate({ params: { path: { id: props.envelope.id, valuationId: props.item.id } } });
    else removeFlow.mutate({ params: { path: { id: props.envelope.id, flowId: props.item.id } } });
  };

  const date = formatLocalDate(props.kind === 'value' ? props.item.asOf : props.item.occurredOn);

  return (
    <SharedRowActions menuId={`correct-${props.kind}-${props.item.id}`}
      menuLabel={props.kind === 'flow' ? t('envelope.row_menu_what', { date, what: flowKindText(t, props.item) }) : t('envelope.row_menu', { date })}
      items={transferLeg ? [] : [{ label: t('envelope.correct'), onSelect: props.onEdit }]}
      confirmation={confirmation} remove={remove} onConfirm={onConfirm}
      present={presentConflict} invalidate={invalidateInvestments} />
  );
}
