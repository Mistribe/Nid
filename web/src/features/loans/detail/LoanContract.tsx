import { useTranslation } from 'react-i18next';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import type { LoanDetail } from '../api';
import { firstPeriodText, insuranceModeText, modeText, money, percent, rateTypeText, roundingText } from '../format';
import { Terms } from './Terms';

const th = 'px-2 py-1 text-left font-medium';
const td = 'px-2 py-1';
const num = 'px-2 py-1 text-right tabular-nums';
const caption = 'text-left font-medium';

/** Spec §7: the contract as read (its edition is 3B2) — the terms, the phases, the insurances. */
export function LoanContract({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const c = loan.contract;
  const m = (amount: string) => money(loan.currency, amount);
  return (
    <div className="space-y-4">
      <Terms items={[
        [t('loan_detail.principal'), m(c.principal)],
        [t('loan_detail.disbursed_on'), formatLocalDate(c.disbursedOn)],
        [t('loan_detail.first_payment'), formatLocalDate(c.firstPaymentDate)],
        [t('loan_detail.duration'), t('loan_detail.duration_value', { n: c.totalPayments })],
        [t('loan_detail.rounding'), roundingText(t, c.rounding)],
        [t('loan_detail.first_period'), firstPeriodText(t, c.firstPeriod)],
      ]} />
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <caption className={caption}>{t('loan_detail.phases')}</caption>
          <thead>
            <tr>
              <th scope="col" className={th}>{t('loan_detail.col_seq')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_mode')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_rate')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_rate_type')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_payments')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_override')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_opening')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_note')}</th>
            </tr>
          </thead>
          <tbody>
            {c.phases.map((p) => (
              <tr key={p.seq} className="border-t">
                <td className={td}>{p.seq}</td>
                <td className={td}>{modeText(t, p.mode)}</td>
                <td className={num}>{percent(t, p.annualRate)}</td>
                <td className={td}>{rateTypeText(t, p.rateType)}</td>
                <td className={td}>{p.payments ?? t('loan_detail.to_term')}</td>
                <td className={num}>{p.paymentOverride ? m(p.paymentOverride) : null}</td>
                <td className={num}>{p.openingBalanceOverride ? m(p.openingBalanceOverride) : null}</td>
                <td className={td}>{p.note}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {c.insurances.length === 0 ? <p className="text-muted-foreground">{t('loan_detail.no_insurance')}</p> : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <caption className={caption}>{t('loan_detail.insurances')}</caption>
            <thead>
              <tr>
                <th scope="col" className={th}>{t('loan_detail.col_insurance_mode')}</th>
                <th scope="col" className={th}>{t('loan_detail.col_insurance_value')}</th>
                <th scope="col" className={th}>{t('loan_detail.col_start')}</th>
                <th scope="col" className={th}>{t('loan_detail.col_end')}</th>
                <th scope="col" className={th}>{t('loan_detail.col_note')}</th>
              </tr>
            </thead>
            <tbody>
              {c.insurances.map((ins, i) => (
                // An insurance has no id on the wire; its rank is stable within one read.
                <tr key={i} className="border-t">
                  <td className={td}>{insuranceModeText(t, ins.mode)}</td>
                  <td className={num}>
                    {ins.monthlyAmount ? t('loan_detail.per_month', { amount: m(ins.monthlyAmount) }) : ins.annualRate ? percent(t, ins.annualRate) : null}
                  </td>
                  <td className={td}><LocalDate value={ins.startsOn} /></td>
                  <td className={td}>{ins.endsOn ? <LocalDate value={ins.endsOn} /> : t('loan_detail.no_end')}</td>
                  <td className={td}>{ins.note}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
