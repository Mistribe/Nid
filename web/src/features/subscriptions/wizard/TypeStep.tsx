import type { UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { OFFERS, thenDefault, type Offer } from './offers';
import type { WizardValues } from './schema';

/** 1E2b1 design §4.1 step 0: the offer, each with its example; choosing one sets the "Et ensuite ?" default. */
export function TypeStep({ form }: { form: UseFormReturn<WizardValues> }) {
  const { t } = useTranslation();
  const current = form.watch('offer');
  const choose = (offer: Offer) => {
    if (offer === current) return;
    form.setValue('offer', offer);
    form.setValue('then', thenDefault(offer));
    form.setValue('offerLabel', ''); // each offer prefills its own label
  };
  return (
    <fieldset className="space-y-2">
      <legend className="text-sm">{t('wizard.offer.legend')}</legend>
      {OFFERS.map((offer) => (
        <label key={offer} className="flex items-start gap-2 rounded-md border p-3">
          <input type="radio" name="offer" value={offer} checked={current === offer} onChange={() => choose(offer)} />
          <span><span className="font-medium">{t(`wizard.offer.${offer}`)}</span>
            <span className="block text-sm text-muted-foreground">{t(`wizard.offer.${offer}_example`)}</span></span>
        </label>
      ))}
    </fieldset>
  );
}
