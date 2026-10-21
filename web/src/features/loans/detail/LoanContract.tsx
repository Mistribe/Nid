import { useTranslation } from 'react-i18next';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { LoanDetail } from '../api';
import { firstPeriodText, insuranceModeText, modeText, money, percent, rateTypeText, roundingText } from '../format';
import { Terms } from './Terms';
import '@/locales/loans';

const num = 'text-right tabular-nums';
const caption = 'mt-0 mb-1 text-left font-medium text-foreground';

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
      <Table className="caption-top">
        <TableCaption className={caption}>{t('loan_detail.phases')}</TableCaption>
        <TableHeader>
          <TableRow>
            <TableHead>{t('loan_detail.col_seq')}</TableHead>
            <TableHead>{t('loan_detail.col_mode')}</TableHead>
            <TableHead>{t('loan_detail.col_rate')}</TableHead>
            <TableHead>{t('loan_detail.col_rate_type')}</TableHead>
            <TableHead>{t('loan_detail.col_payments')}</TableHead>
            <TableHead>{t('loan_detail.col_override')}</TableHead>
            <TableHead>{t('loan_detail.col_opening')}</TableHead>
            <TableHead>{t('loan_detail.col_note')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {c.phases.map((p) => (
            <TableRow key={p.seq}>
              <TableCell>{p.seq}</TableCell>
              <TableCell>{modeText(t, p.mode)}</TableCell>
              <TableCell className={num}>{percent(t, p.annualRate)}</TableCell>
              <TableCell>{rateTypeText(t, p.rateType)}</TableCell>
              <TableCell>{p.payments ?? t('loan_detail.to_term')}</TableCell>
              <TableCell className={num}>{p.paymentOverride ? m(p.paymentOverride) : null}</TableCell>
              <TableCell className={num}>{p.openingBalanceOverride ? m(p.openingBalanceOverride) : null}</TableCell>
              <TableCell className="whitespace-normal">{p.note}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {c.insurances.length === 0 ? <p className="text-muted-foreground">{t('loan_detail.no_insurance')}</p> : (
        <Table className="caption-top">
          <TableCaption className={caption}>{t('loan_detail.insurances')}</TableCaption>
          <TableHeader>
            <TableRow>
              <TableHead>{t('loan_detail.col_insurance_mode')}</TableHead>
              <TableHead>{t('loan_detail.col_insurance_value')}</TableHead>
              <TableHead>{t('loan_detail.col_start')}</TableHead>
              <TableHead>{t('loan_detail.col_end')}</TableHead>
              <TableHead>{t('loan_detail.col_note')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {c.insurances.map((ins, i) => (
              // An insurance has no id on the wire; its rank is stable within one read.
              <TableRow key={i}>
                <TableCell>{insuranceModeText(t, ins.mode)}</TableCell>
                <TableCell className={num}>
                  {ins.monthlyAmount ? t('loan_detail.per_month', { amount: m(ins.monthlyAmount) }) : ins.annualRate ? percent(t, ins.annualRate) : null}
                </TableCell>
                <TableCell><LocalDate value={ins.startsOn} /></TableCell>
                <TableCell>{ins.endsOn ? <LocalDate value={ins.endsOn} /> : t('loan_detail.no_end')}</TableCell>
                <TableCell className="whitespace-normal">{ins.note}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}
