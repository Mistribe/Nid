import { Controller, useFormContext } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Input } from '@/components/ui/input';
import { Folded } from '../../loans/new/Folded';
import { todayIso } from '../../subscriptions/today';
import { CADENCES, PROPERTY_TYPES } from '../format';
import type { PropertyFieldsValues } from './propertyForm';
import '@/locales/loans';

type AmountField = 'purchasePrice' | 'notaryFees' | 'agencyFees' | 'otherAcquisitionCosts';

/** What the property is: its type and its locality. */
export function IdentityFields() {
  const { t } = useTranslation();
  const form = useFormContext<PropertyFieldsValues>();
  const e = form.formState.errors;
  return (
    <div className="grid gap-3 sm:grid-cols-2">
      <Controller control={form.control} name="type" render={({ field }) => (
        <Field label={t('property_form.type')} error={e.type?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
            options={PROPERTY_TYPES.map((v) => ({ value: v, label: t(`property_form.type_option.${v}`) }))} />}
        </Field>
      )} />
      <Field label={t('property_form.location')} hint={t('property_form.location_help')} error={e.locationLabel?.message}>
        {(c) => <Input {...c} {...form.register('locationLabel')} />}
      </Field>
    </div>
  );
}

/**
 * The purchase and the estimate rhythm (3D1 design §5–§6): the amounts on a grid, the rhythm and its threshold folded in
 * « Plus d’options », which opens itself on an error inside.
 */
export function PurchaseFields() {
  const { t } = useTranslation();
  const form = useFormContext<PropertyFieldsValues>();
  const e = form.formState.errors;
  const amount = (name: AmountField, label: string) => (
    <Field label={label} error={e[name]?.message}>
      {(c) => <Input inputMode="decimal" {...c} {...form.register(name)} />}
    </Field>
  );

  return (
    <>
      <div className="grid gap-3 sm:grid-cols-3">
        <Controller control={form.control} name="purchaseDate" render={({ field }) => (
          <Field label={t('property_form.purchase_date')} error={e.purchaseDate?.message}>
            {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
        {amount('purchasePrice', t('property_form.purchase_price'))}
        {amount('notaryFees', t('property_form.notary_fees'))}
        {amount('agencyFees', t('property_form.agency_fees'))}
        {amount('otherAcquisitionCosts', t('property_form.other_costs'))}
        <Field label={t('property_form.surface')} hint={t('loan_form.optional')} error={e.surfaceM2?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('surfaceM2')} />}
        </Field>
      </div>
      <Folded label={t('loan_form.more_options')} hasError={Boolean(e.valuationCadence || e.staleAfterDays)}>
        <div className="grid gap-3 sm:grid-cols-2">
          <Controller control={form.control} name="valuationCadence" render={({ field }) => (
            <Field label={t('property_form.cadence')} error={e.valuationCadence?.message}>
              {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                options={CADENCES.map((v) => ({ value: v, label: t(`property_form.cadence_option.${v}`) }))} />}
            </Field>
          )} />
          <Field label={t('property_form.stale_after')} hint={t('property_form.stale_after_help')} error={e.staleAfterDays?.message}>
            {(c) => <Input inputMode="numeric" {...c} {...form.register('staleAfterDays')} />}
          </Field>
        </div>
      </Folded>
    </>
  );
}

/** The property's own fields but its name and note, shared by the settings: they read the form from a `FormProvider`. */
export function PropertyFields() {
  return (
    <>
      <IdentityFields />
      <PurchaseFields />
    </>
  );
}
