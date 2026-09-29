import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Reload } from '../../subscriptions/edits/Reload';
import { FLOW, invalidateInvestments, VALUATION, type EnvelopeDetail, type Flow, type Valuation } from '../api';
import { isTransferLeg } from '../format';

type RowActionsProps =
  | { kind: 'value'; envelope: EnvelopeDetail; item: Valuation; onEdit: () => void }
  | { kind: 'flow'; envelope: EnvelopeDetail; item: Flow; onEdit: () => void };

/**
 * A value's or a flow's row actions (2B2a design §6): « Corriger » swaps the row for its edit form; « Supprimer »
 * asks first, then deletes. A transfer leg (`category = transfer`, 2B2b owns transfers) cannot be corrected here —
 * only deleted, with a warning that its other leg keeps its own.
 */
export function RowActions(props: RowActionsProps) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [deleted, setDeleted] = useState(false);
  const transferLeg = props.kind === 'flow' && isTransferLeg(props.item);

  const removeValuation = $api.useMutation('delete', VALUATION, {
    onSuccess: () => { setDeleted(true); void invalidateInvestments(queryClient); },
  });
  const removeFlow = $api.useMutation('delete', FLOW, {
    onSuccess: () => { setDeleted(true); void invalidateInvestments(queryClient); },
  });
  const remove = props.kind === 'value' ? removeValuation : removeFlow;

  const confirmation = props.kind === 'value'
    ? t('envelope.delete_value')
    : transferLeg ? t('envelope.delete_transfer_leg') : t('envelope.delete_flow');

  const onConfirm = () => {
    setDeleted(false);
    if (props.kind === 'value') removeValuation.mutate({ params: { path: { id: props.envelope.id, valuationId: props.item.id } } });
    else removeFlow.mutate({ params: { path: { id: props.envelope.id, flowId: props.item.id } } });
  };

  const problem = remove.error ? present(toProblem(remove.error)) : null;

  return (
    <span className="flex flex-wrap items-center gap-2">
      {!transferLeg ? <Button type="button" variant="outline" size="sm" onClick={props.onEdit}>{t('envelope.correct')}</Button> : null}
      <ConfirmAction label={t('envelope.delete')} confirmation={confirmation} pending={remove.isPending} onConfirm={onConfirm} />
      {deleted ? <span role="status">{t('envelope.deleted')}</span> : null}
      {problem ? <><ProblemMessage problem={problem} />{problem.kind === 'conflict' ? <Reload /> : null}</> : null}
    </span>
  );
}
