import { useTranslation } from 'react-i18next';
import { Section } from '@/components/page/Section';
import { AmountField, type SectionProps } from './fields';
import '@/locales/tax';

/** The 2025 aid donations have two boxes: 7UD until 13 October, 7UQ from 14 October (loi de finances pour 2026, art. 28). */
const SPLIT_AID_YEAR = 2025;

/** The year's expenses that open a credit or a reduction, with their 2042 boxes. */
export function ExpensesSection({ estimate, draft }: SectionProps) {
  const split = estimate.year === SPLIT_AID_YEAR;
  const { t } = useTranslation();
  return (
    <Section id="depenses" title={t('tax.profile.expenses')}>
      <div className="grid gap-3 sm:grid-cols-2">
        <AmountField draft={draft} name="childcareOutsideHome" hint={t('tax.profile.childcare_hint')} badge="7GA" />
        <AmountField draft={draft} name="homeEmployment" hint={t('tax.profile.home_hint')} badge="7DB" />
        <AmountField draft={draft} name="donations" badge="7UF" />
        <AmountField draft={draft} name="donationsAid" hint={t(split ? 'tax.profile.aid_hint_early' : 'tax.profile.aid_hint')} badge="7UD" />
        {split && <AmountField draft={draft} name="donationsAidLate" hint={t('tax.profile.aid_hint_late')} badge="7UQ" />}
      </div>
    </Section>
  );
}
