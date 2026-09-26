import { useTranslation } from 'react-i18next';
import { AmountField, DateField, DurationFields, LabelField, PayerFields, type Form, type Person } from './fields';

/** 1E2b1 design §4.1: a gift card, received (paid by someone outside the household) or bought. */
export function Gift({ form, persons }: { form: Form; persons: Person[] }) {
  const { t } = useTranslation();
  const received = form.watch('giftReceived');
  return (
    <div className="space-y-4">
      <fieldset className="space-y-1">
        <legend className="text-sm">{t('wizard.offer.gift_origin')}</legend>
        {([true, false] as const).map((r) => (
          <label key={String(r)} className="flex items-center gap-2">
            <input type="radio" name="giftReceived" checked={received === r} onChange={() => form.setValue('giftReceived', r)} />
            {t(r ? 'wizard.offer.gift_received' : 'wizard.offer.gift_bought')}
          </label>
        ))}
      </fieldset>
      <DateField form={form} name="offerDate" label={t('wizard.offer.date')} />
      <LabelField form={form} initial={t('wizard.offer.gift_label')} />
      <DurationFields form={form} count="durationCount" unit="durationUnit" label={t('wizard.offer.duration')} />
      {received ? null : (
        <>
          <AmountField form={form} name="offerAmount" label={t('wizard.offer.price')} />
          <PayerFields form={form} persons={persons} />
        </>
      )}
    </div>
  );
}
