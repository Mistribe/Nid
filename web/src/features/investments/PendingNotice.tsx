import { Link } from '@tanstack/react-router';
import { useQueryClient } from '@tanstack/react-query';
import { Clock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { APPROVE, type ProposalView } from '@/api/proposals';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { formatMoney } from '@/components/Money';
import { formatLocalDate } from '@/components/LocalDate';

const VALUATION = 'investments.record_valuation';
/** Bank sync's proposals carry this source label (`BankSyncCollector.SourceLabel`). */
const BANK = 'Banque';

type Valuation = { asOf: string; amount: { amount: string; currency: string } };

/** #373: a value bank sync proposed for this envelope and the household has not approved yet. */
export const isBankValuation = (p: ProposalView) => p.kind === VALUATION && p.sourceLabel === BANK;

/** #373: a pending value proposal, whatever proposed it. */
export const isValuation = (p: ProposalView) => p.kind === VALUATION;

/**
 * #373: what awaits approval on this envelope, under its header. One value: its amount and date, approved in place by those who write the
 * envelope (as « À valider » does), or opened there. Several proposals: their count and the list narrowed to this envelope.
 */
export function PendingNotice({ envelope, pending, canApprove }: { envelope: string; pending: ProposalView[]; canApprove: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const approve = $api.useMutation('post', APPROVE);
  if (pending.length === 0) return null;

  const first = pending[0];
  const one = pending.length === 1 && first && isValuation(first) ? first : null;
  const value = one ? (one.editedPayload ?? one.payload) as Valuation : null;
  const text = one && value
    ? t(isBankValuation(one) ? 'envelope.pending.bank_value' : 'envelope.pending.value', {
      amount: formatMoney(value.amount), date: formatLocalDate(value.asOf, 'short') })
    : t('envelope.pending.many', { count: pending.length });
  const accept = (p: ProposalView) => approve.mutate({ params: { path: { id: p.id } }, body: { version: p.version } }, {
    onSuccess: () => {
      toast.success(t('envelope.pending.approved'));
      // An approval writes through the kind's handler: the figures, the values and the queue all change.
      void queryClient.invalidateQueries();
    },
  });

  return (
    <div role="note" aria-label={t('envelope.pending.label')} className="space-y-2 rounded-lg bg-accent px-3 py-2 text-accent-foreground">
      <p className="flex flex-wrap items-center gap-2">
        <Clock aria-hidden="true" className="size-4" />
        <span>{text}</span>
        {one && canApprove ? (
          <Button type="button" size="sm" disabled={approve.isPending} onClick={() => accept(one)}>{t('envelope.pending.approve')}</Button>
        ) : null}
        {one
          ? <Link to="/proposals/$id" params={{ id: one.id }} className="text-sm underline">{t('envelope.pending.see')}</Link>
          : <Link to="/proposals" search={{ target: envelope }} className="text-sm underline">{t('envelope.pending.see')}</Link>}
      </p>
      {approve.error ? <ProblemMessage problem={present(toProblem(approve.error))} /> : null}
    </div>
  );
}
