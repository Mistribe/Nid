import { Controller, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { INSURANCE_MODES } from '../format';
import type { ContractValues } from './loanForm';
import '@/locales/loans';

/** One insurance (spec §6): its mode, then the monthly amount or the rate in % as the mode asks, its dates and note. */
export function InsuranceFields({ form, index }: { form: UseFormReturn<ContractValues>; index: number }) {
  const { t } = useTranslation();
  const e = form.formState.errors.insurances?.[index];
  const mode = form.watch(`insurances.${index}.mode`);
  return (
    <>
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name={`insurances.${index}.mode`} render={({ field }) => (
          <Field label={t('loan_form.insurance_mode')} tip={t('loan_form.insurance_mode_tip')} error={e?.mode?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={INSURANCE_MODES.map((m) => ({ value: m, label: t(`loan_form.insurance_mode_option.${m}`) }))} />}
          </Field>
        )} />
        {mode === 'fixed_monthly' ? (
          <Field label={t('loan_form.insurance_amount')} error={e?.amount?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register(`insurances.${index}.amount`)} />}
          </Field>
        ) : (
          <Field label={t('loan_form.insurance_rate')} error={e?.rate?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register(`insurances.${index}.rate`)} />}
          </Field>
        )}
        <Controller control={form.control} name={`insurances.${index}.startsOn`} render={({ field }) => (
          <Field label={t('loan_form.starts_on')} error={e?.startsOn?.message}>
            {(c) => <DateField {...c} value={field.value}
              onChange={(v) => { field.onChange(v); form.setValue(`insurances.${index}.follows`, false); }} />}
          </Field>
        )} />
        <Controller control={form.control} name={`insurances.${index}.endsOn`} render={({ field }) => (
          <Field label={t('loan_form.ends_on')} hint={t('loan_form.optional')} error={e?.endsOn?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
      </div>
      <Field label={t('loan_form.insurance_note')} error={e?.note?.message}>
        {(c) => <Textarea {...c} {...form.register(`insurances.${index}.note`)} />}
      </Field>
    </>
  );
}
