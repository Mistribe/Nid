import { useTranslation } from 'react-i18next';
import { Section } from '@/components/page/Section';
import { AmountField, type SectionProps } from './fields';
import '@/locales/tax';

/** The year's expenses that open a credit or a reduction, with their 2042 boxes. */
export function ExpensesSection({ draft }: SectionProps) {
  const { t } = useTranslation();
  return (
    <Section id="depenses" title={t('tax.profile.expenses')}>
      <div className="grid gap-3 sm:grid-cols-2">
        <AmountField draft={draft} name="childcareOutsideHome" hint={t('tax.profile.childcare_hint')} badge="7GA" />
        <AmountField draft={draft} name="homeEmployment" hint={t('tax.profile.home_hint')} badge="7DB" />
        <AmountField draft={draft} name="donations" badge="7UF" />
        <AmountField draft={draft} name="donationsAid" hint={t('tax.profile.aid_hint')} badge="7UD" />
      </div>
    </Section>
  );
}
