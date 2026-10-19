import { Controller, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { PHASE_MODES, RATE_TYPES } from '../format';
import { Folded } from './Folded';
import type { ContractValues } from './loanForm';

/** One line of the phase list (spec §6): mode, rate in %, fixed or variable, its count; the rest under « Plus d’options ». */
export function PhaseFields({ form, index, last }: { form: UseFormReturn<ContractValues>; index: number; last: boolean }) {
  const { t } = useTranslation();
  const e = form.formState.errors.phases?.[index];
  return (
    <>
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name={`phases.${index}.mode`} render={({ field }) => (
          <Field label={t('loan_form.mode')} error={e?.mode?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={PHASE_MODES.map((m) => ({ value: m, label: t(`loan_form.mode_option.${m}`) }))} />}
          </Field>
        )} />
        <Field label={t('loan_form.rate')} error={e?.rate?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register(`phases.${index}.rate`)} />}
        </Field>
        <Controller control={form.control} name={`phases.${index}.rateType`} render={({ field }) => (
          <Field label={t('loan_form.rate_type')} error={e?.rateType?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={RATE_TYPES.map((r) => ({ value: r, label: t(`loan_form.rate_type_option.${r}`) }))} />}
          </Field>
        )} />
        <Field label={t('loan_form.payments')} hint={last ? t('loan_form.payments_help') : undefined} error={e?.payments?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register(`phases.${index}.payments`)} />}
        </Field>
      </div>
      <Folded label={t('loan_form.more_options')} hasError={Boolean(e?.paymentOverride || e?.openingBalance || e?.note)}>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('loan_form.payment_override')} hint={t('loan_form.optional')} error={e?.paymentOverride?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register(`phases.${index}.paymentOverride`)} />}
          </Field>
          <Field label={t('loan_form.opening_balance')} hint={t('loan_form.optional')} error={e?.openingBalance?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register(`phases.${index}.openingBalance`)} />}
          </Field>
        </div>
        <Field label={t('loan_form.phase_note')} error={e?.note?.message}>
          {(c) => <Textarea {...c} {...form.register(`phases.${index}.note`)} />}
        </Field>
      </Folded>
    </>
  );
}
