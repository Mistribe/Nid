import { useFormContext } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Folded } from '../../loans/new/Folded';
import { Field } from '../../subscriptions/edits/Field';
import { todayIso } from '../../subscriptions/today';
import { CADENCES, PROPERTY_TYPES } from '../format';
import type { PropertyFieldsValues } from './newProperty';

const input = 'rounded-md border px-3 py-2';
type AmountField = 'purchasePrice' | 'notaryFees' | 'agencyFees' | 'otherAcquisitionCosts';

/**
 * The property's own fields but its name and note (3D1 design §5–§6), shared by the creation and the settings: they read
 * the form from a `FormProvider` whose values extend `PropertyFieldsValues`. The estimate rhythm and its threshold are
 * folded in « Plus d’options », which opens itself on an error inside.
 */
export function PropertyFields() {
  const { t } = useTranslation();
  const form = useFormContext<PropertyFieldsValues>();
  const e = form.formState.errors;
  const amount = (name: AmountField, label: string) => (
    <Field label={label} error={e[name]?.message}>
      {(c) => <input inputMode="decimal" {...c} {...form.register(name)} className={`w-40 ${input}`} />}
    </Field>
  );

  return (
    <>
      <Field label={t('property_form.type')} error={e.type?.message}>
        {(c) => (
          <select {...c} {...form.register('type')} className={input}>
            {PROPERTY_TYPES.map((v) => <option key={v} value={v}>{t(`property_form.type_option.${v}`)}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('property_form.purchase_date')} error={e.purchaseDate?.message}>
        {(c) => <input type="date" max={todayIso()} {...c} {...form.register('purchaseDate')} className={input} />}
      </Field>
      {amount('purchasePrice', t('property_form.purchase_price'))}
      {amount('notaryFees', t('property_form.notary_fees'))}
      {amount('agencyFees', t('property_form.agency_fees'))}
      {amount('otherAcquisitionCosts', t('property_form.other_costs'))}
      <Field label={t('property_form.surface')} hint={t('loan_form.optional')} error={e.surfaceM2?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('surfaceM2')} className={`w-24 ${input}`} />}
      </Field>
      <Field label={t('property_form.location')} hint={t('property_form.location_help')} error={e.locationLabel?.message}>
        {(c) => <input {...c} {...form.register('locationLabel')} className={`w-full ${input}`} />}
      </Field>
      <Folded label={t('loan_form.more_options')} hasError={Boolean(e.valuationCadence || e.staleAfterDays)}>
        <Field label={t('property_form.cadence')} error={e.valuationCadence?.message}>
          {(c) => (
            <select {...c} {...form.register('valuationCadence')} className={input}>
              {CADENCES.map((v) => <option key={v} value={v}>{t(`property_form.cadence_option.${v}`)}</option>)}
            </select>
          )}
        </Field>
        <Field label={t('property_form.stale_after')} hint={t('property_form.stale_after_help')} error={e.staleAfterDays?.message}>
          {(c) => <input inputMode="numeric" {...c} {...form.register('staleAfterDays')} className={`w-24 ${input}`} />}
        </Field>
      </Folded>
    </>
  );
}
