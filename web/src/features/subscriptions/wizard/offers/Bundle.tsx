import { useId } from 'react';
import { Controller, useFieldArray } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { SelectField } from '@/components/form/SelectField';
import { Button } from '@/components/ui/button';
import { LIST } from '../../api';
import { AmountField, DateField, DurationFields, LabelField, PayerFields, type Form, type Person } from './fields';

function Other({ form, index, options, onRemove }: {
  form: Form; index: number; options: { id: string; displayName: string }[]; onRemove: () => void;
}) {
  const { t } = useTranslation();
  const legendId = useId();
  const errorId = useId();
  const label = t('wizard.offer.covered', { n: index + 1 });
  const error = form.formState.errors.others?.[index]?.subscriptionId?.message;
  return (
    <fieldset className="space-y-2 rounded-md border p-3">
      <legend id={legendId} className="text-sm">{label}</legend>
      <Controller control={form.control} name={`others.${index}.subscriptionId`} render={({ field }) => (
        // The legend names the select (`aria-labelledby` is not part of FieldControl: a spread carries it to the trigger).
        <SelectField {...{ 'aria-labelledby': legendId }} id={`${legendId}-select`} aria-describedby={error ? errorId : undefined}
          aria-invalid={error ? true : undefined} value={field.value} onValueChange={field.onChange}
          options={[{ value: '', label: t('wizard.none') }, ...options.map((o) => ({ value: o.id, label: o.displayName }))]} />
      )} />
      {error ? <span id={errorId} className="block text-sm text-destructive">{error}</span> : null}
      <DurationFields form={form} count={`others.${index}.count`} unit={`others.${index}.unit`} label={t('wizard.offer.included')} />
      <Button type="button" variant="ghost" size="sm" onClick={onRemove}>{t('wizard.offer.remove_covered')}</Button>
    </fieldset>
  );
}

/** 1E2b1 design §4.1: a purchase that includes this subscription, and maybe others of the household. */
export function Bundle({ form, persons }: { form: Form; persons: Person[] }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const others = useFieldArray({ control: form.control, name: 'others' });
  const list = $api.useQuery('get', LIST, { params: { query: { scope: 'me' } } });
  const options = list.data?.items ?? [];
  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2">
        <DateField form={form} name="offerDate" label={t('wizard.offer.purchase_date')} />
        <LabelField form={form} initial={t('wizard.offer.bundle')} />
      </div>
      <AmountField form={form} name="offerAmount" label={t('wizard.offer.total_price')} withCurrency />
      <PayerFields form={form} persons={persons} />
      <AmountField form={form} name="allocated" label={t('wizard.offer.allocated')} hint={t('wizard.offer.allocated_hint')} />
      <DurationFields form={form} count="durationCount" unit="durationUnit" label={t('wizard.offer.included_here')} />
      <fieldset className="space-y-2">
        <legend className="text-sm">{t('wizard.offer.covers_also')}</legend>
        {others.fields.map((row, i) => (
          <Other key={row.id} form={form} index={i} options={options} onRemove={() => others.remove(i)} />
        ))}
        <Button type="button" variant="outline" size="sm" onClick={() => others.append({ subscriptionId: '', count: '1', unit: 'month' })}>
          {t('wizard.offer.add_covered')}
        </Button>
      </fieldset>
    </div>
  );
}
