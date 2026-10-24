import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { ScheduleTable } from '../loans/detail/ScheduleTable';
import type { ScenarioResult } from './api';
import '@/locales/scenarios';

/** Spec §7 « Prêts »: each loan of the scenario, its cost then its schedule, with the loans screen's own table. */
export default function LoansTab({ result }: { result: ScenarioResult }) {
  const { t } = useTranslation();
  return (
    <>
      {result.loans.map((loan, i) => (
        <Section key={`${loan.name}-${i}`} title={loan.name}>
          <p className="text-sm text-muted-foreground">{t('scenarios.detail.loan_cost', {
            interest: formatMoney(loan.interest), insurance: formatMoney(loan.insurance), fees: formatMoney(loan.fees),
          })}</p>
          <ScheduleTable schedule={loan.schedule} currency={loan.interest.currency} />
        </Section>
      ))}
    </>
  );
}
