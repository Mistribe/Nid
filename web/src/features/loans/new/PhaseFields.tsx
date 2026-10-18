import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import type { UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Field } from '../../subscriptions/edits/Field';
import { PHASE_MODES, RATE_TYPES } from '../format';
import { Folded } from './Folded';
import type { ContractValues } from './loanForm';

const input = 'rounded-md border px-3 py-2';

/** One line of the phase list (spec §6): mode, rate in %, fixed or variable, its count; the rest under « Plus d’options ». */
export function PhaseFields({ form, index, last }: { form: UseFormReturn<ContractValues>; index: number; last: boolean }) {
  const { t } = useTranslation();
  const e = form.formState.errors.phases?.[index];
  return (
    <>
      <Field label={t('loan_form.mode')} error={e?.mode?.message}>
        {(c) => (
          <NativeSelect {...c} {...form.register(`phases.${index}.mode`)}>
            {PHASE_MODES.map((m) => <NativeSelectOption key={m} value={m}>{t(`loan_form.mode_option.${m}`)}</NativeSelectOption>)}
          </NativeSelect>
        )}
      </Field>
      <Field label={t('loan_form.rate')} error={e?.rate?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register(`phases.${index}.rate`)} className={`w-24 ${input}`} />}
      </Field>
      <Field label={t('loan_form.rate_type')} error={e?.rateType?.message}>
        {(c) => (
          <NativeSelect {...c} {...form.register(`phases.${index}.rateType`)}>
            {RATE_TYPES.map((r) => <NativeSelectOption key={r} value={r}>{t(`loan_form.rate_type_option.${r}`)}</NativeSelectOption>)}
          </NativeSelect>
        )}
      </Field>
      <Field label={t('loan_form.payments')} hint={last ? t('loan_form.payments_help') : undefined} error={e?.payments?.message}>
        {(c) => <input inputMode="numeric" {...c} {...form.register(`phases.${index}.payments`)} className={`w-24 ${input}`} />}
      </Field>
      <Folded label={t('loan_form.more_options')} hasError={Boolean(e?.paymentOverride || e?.openingBalance || e?.note)}>
        <Field label={t('loan_form.payment_override')} hint={t('loan_form.optional')} error={e?.paymentOverride?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register(`phases.${index}.paymentOverride`)} className={`w-32 ${input}`} />}
        </Field>
        <Field label={t('loan_form.opening_balance')} hint={t('loan_form.optional')} error={e?.openingBalance?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register(`phases.${index}.openingBalance`)} className={`w-32 ${input}`} />}
        </Field>
        <Field label={t('loan_form.phase_note')} error={e?.note?.message}>
          {(c) => <textarea {...c} {...form.register(`phases.${index}.note`)} className="w-full" />}
        </Field>
      </Folded>
    </>
  );
}
