import { useTranslation } from 'react-i18next';
import { AmountField, Choice, DateField, DurationFields, LabelField, PayerFields, type Form, type Person } from './fields';

/** 1E2b1 design §4.1: a gift card, received (paid by someone outside the household) or bought. */
export function Gift({ form, persons }: { form: Form; persons: Person[] }) {
  const { t } = useTranslation();
  const received = form.watch('giftReceived');
  return (
    <div className="space-y-4">
      <Choice label={t('wizard.offer.gift_origin')} value={String(received)} onValueChange={(v) => form.setValue('giftReceived', v === 'true')}
        options={[{ value: 'true', label: t('wizard.offer.gift_received') }, { value: 'false', label: t('wizard.offer.gift_bought') }]} />
      <div className="grid gap-3 sm:grid-cols-2">
        <DateField form={form} name="offerDate" label={t('wizard.offer.date')} />
        <LabelField form={form} initial={t('wizard.offer.gift_label')} />
      </div>
      <DurationFields form={form} count="durationCount" unit="durationUnit" label={t('wizard.offer.duration')} />
      {received ? null : (
        <>
          <AmountField form={form} name="offerAmount" label={t('wizard.offer.price')} withCurrency />
          <PayerFields form={form} persons={persons} />
        </>
      )}
    </div>
  );
}
