import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { StatLine } from '@/components/page/StatLine';
import { Progress } from '@/components/ui/progress';
import { toPlot } from '../../investments/chart/plot';
import type { LoanDetail } from '../api';
import { money } from '../format';
import { fractionToPercent } from '../rate';
import { Terms } from './Terms';

/** Spec §5.2: where the loan stands today, every figure the server's, inline under the title. */
export function LoanStats({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const f = loan.figures;
  const next = f.nextPaymentOn && f.nextPaymentTotal
    ? t('loan_detail.next_payment_value', { date: formatLocalDate(f.nextPaymentOn), amount: money(loan.currency, f.nextPaymentTotal) })
    : t('loan_detail.no_next_payment');
  // An ended loan has no current phase (spec §7, precision 9), although the server still names its last one.
  const rate = !loan.ended && f.currentRate && f.currentPhaseSeq != null
    ? t('loan_detail.current_rate_value', { rate: fractionToPercent(f.currentRate), phase: f.currentPhaseSeq })
    : null;
  const end = f.endDate
    ? t('loan_detail.end_value', { date: formatLocalDate(f.endDate), count: f.paymentsLeft })
    : t('loan_detail.payments_left', { count: f.paymentsLeft });
  return (
    <>
      <StatLine items={[
        { label: t('loan_detail.remaining'), value: money(loan.currency, f.balance) },
        { label: t('loan_detail.next_payment'), value: next },
        ...(rate ? [{ label: t('loan_detail.current_rate'), value: rate }] : []),
        { label: t('loan_detail.end'), value: end },
      ]} />
      {f.nextPaymentTotal ? <p className="text-sm text-muted-foreground">{t('loan_detail.insurance_included')}</p> : null}
    </>
  );
}

/**
 * The part of the principal already repaid: the bar is sized by plotting numbers (`toPlot`), what a person reads is the
 * server's two strings, never a percentage computed here.
 */
export function LoanProgress({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const total = toPlot(loan.contract.principal);
  const share = total > 0 ? Math.min(100, (toPlot(loan.figures.principalRepaid) / total) * 100) : 0;
  const label = t('loan_detail.progress', { repaid: money(loan.currency, loan.figures.principalRepaid), principal: money(loan.currency, loan.contract.principal) });
  return (
    <div className="space-y-2">
      <Progress value={share} aria-label={label} getValueLabel={() => label} />
      <p className="text-sm">{label}</p>
    </div>
  );
}

/** What the credit costs: paid so far and still to pay. */
export function LoanCost({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const f = loan.figures;
  const m = (amount: string) => money(loan.currency, amount);
  return (
    <Terms items={[
      [t('loan_detail.interest_paid'), m(f.interestPaid)],
      [t('loan_detail.insurance_paid'), m(f.insurancePaid)],
      [t('loan_detail.fees_paid'), m(f.feesPaid)],
      [t('loan_detail.total_cost'), m(f.totalCost)],
      [t('loan_detail.cost_paid'), m(f.costPaid)],
      [t('loan_detail.cost_remaining'), m(f.costRemaining)],
    ]} />
  );
}
