import { useTranslation } from 'react-i18next';
import { AmountField, DateField, DurationFields, LabelField, PayerFields, type Form, type Person } from './fields';

/** 1E2b1 design §4.1: a term paid upfront, with its free months. */
export function Prepaid({ form, persons }: { form: Form; persons: Person[] }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-4">
      <DateField form={form} name="offerDate" label={t('wizard.offer.purchase_date')} />
      <LabelField form={form} initial={t('wizard.offer.prepaid_label', { name: form.getValues('displayName').trim() })} />
      <AmountField form={form} name="offerAmount" label={t('wizard.offer.paid_price')} />
      <PayerFields form={form} persons={persons} />
      <DurationFields form={form} count="paidCount" unit="paidUnit" label={t('wizard.offer.paid_duration')} />
      <DurationFields form={form} count="bonusCount" unit="bonusUnit" label={t('wizard.offer.bonus_duration')} hint={t('wizard.offer.bonus_hint')} />
    </div>
  );
}
