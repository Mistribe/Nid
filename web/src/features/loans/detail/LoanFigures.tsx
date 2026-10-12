import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import type { LoanDetail } from '../api';
import { money } from '../format';
import { fractionToPercent } from '../rate';
import { Terms } from './Terms';

/** Spec §7: the loan today, every figure the server's. */
export function LoanFigures({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const f = loan.figures;
  const m = (amount: string) => money(loan.currency, amount);
  const next = f.nextPaymentOn && f.nextPaymentTotal
    ? t('loan_detail.next_payment_value', { date: formatLocalDate(f.nextPaymentOn), amount: m(f.nextPaymentTotal) })
    : t('loan_detail.no_next_payment');
  // An ended loan has no current phase (spec §7, precision 9), although the server still names its last one.
  const rate = !loan.ended && f.currentRate && f.currentPhaseSeq != null
    ? t('loan_detail.current_rate_value', { rate: fractionToPercent(f.currentRate), phase: f.currentPhaseSeq })
    : null;
  const items: [string, string][] = [
    [t('loan_detail.balance'), m(f.balance)],
    [t('loan_detail.principal'), m(loan.contract.principal)],
    [t('loan_detail.next_payment'), next],
    ...(rate ? [[t('loan_detail.current_rate'), rate] as [string, string]] : []),
    [t('loan_detail.payments_left'), String(f.paymentsLeft)],
    ...(f.endDate ? [[t('loan_detail.end_date'), formatLocalDate(f.endDate)] as [string, string]] : []),
    [t('loan_detail.principal_repaid'), m(f.principalRepaid)],
    [t('loan_detail.interest_paid'), m(f.interestPaid)],
    [t('loan_detail.insurance_paid'), m(f.insurancePaid)],
    [t('loan_detail.fees_paid'), m(f.feesPaid)],
    [t('loan_detail.total_cost'), m(f.totalCost)],
    [t('loan_detail.cost_paid'), m(f.costPaid)],
    [t('loan_detail.cost_remaining'), m(f.costRemaining)],
  ];
  return <Terms items={items} />;
}
