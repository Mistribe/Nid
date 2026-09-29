import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Reload } from '../../subscriptions/edits/Reload';
import { FLOW, invalidateInvestments, VALUATION, type EnvelopeDetail, type Flow, type Valuation } from '../api';
import { isTransferLeg } from '../format';

type RowActionsProps = {
  envelope: EnvelopeDetail;
  onEdit: () => void;
  /** The deletion's confirmation message, for the section's own live region: the row (and this very component)
   * disappears once the refetch lands, so the status and the focus it moves belong to the page, not to this
   * soon-gone one. */
  onDeleted: (message: string) => void;
} & ({ kind: 'value'; item: Valuation } | { kind: 'flow'; item: Flow });

/**
 * A value's or a flow's row actions (2B2a design §6): « Corriger » swaps the row for its edit form; « Supprimer »
 * asks first, then deletes. A transfer leg (`category = transfer`, 2B2b owns transfers) cannot be corrected here —
 * only deleted, with a warning that its other leg keeps its own.
 */
export function RowActions(props: RowActionsProps) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const transferLeg = props.kind === 'flow' && isTransferLeg(props.item);

  const removeValuation = $api.useMutation('delete', VALUATION, {
    onSuccess: () => { void invalidateInvestments(queryClient); props.onDeleted(t('envelope.deleted')); },
  });
  const removeFlow = $api.useMutation('delete', FLOW, {
    onSuccess: () => { void invalidateInvestments(queryClient); props.onDeleted(t('envelope.deleted')); },
  });
  const remove = props.kind === 'value' ? removeValuation : removeFlow;

  const confirmation = props.kind === 'value'
    ? t('envelope.delete_value')
    : transferLeg ? t('envelope.delete_transfer_leg') : t('envelope.delete_flow');

  const onConfirm = () => {
    if (props.kind === 'value') removeValuation.mutate({ params: { path: { id: props.envelope.id, valuationId: props.item.id } } });
    else removeFlow.mutate({ params: { path: { id: props.envelope.id, flowId: props.item.id } } });
  };

  const problem = remove.error ? present(toProblem(remove.error)) : null;

  return (
    <span className="flex flex-wrap items-center gap-2">
      {!transferLeg ? <Button type="button" variant="outline" size="sm" onClick={props.onEdit}>{t('envelope.correct')}</Button> : null}
      <ConfirmAction label={t('envelope.delete')} confirmation={confirmation} pending={remove.isPending} onConfirm={onConfirm} />
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? (
            <Reload onReload={() => { remove.reset(); return invalidateInvestments(queryClient); }} />
          ) : null}
        </>
      ) : null}
    </span>
  );
}
