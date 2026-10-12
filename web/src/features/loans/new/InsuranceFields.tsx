import type { UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Field } from '../../subscriptions/edits/Field';
import { INSURANCE_MODES } from '../format';
import type { NewLoanValues } from './newLoan';

const input = 'rounded-md border px-3 py-2';

/** One insurance (spec §6): its mode, then the monthly amount or the rate in % as the mode asks, its dates and note. */
export function InsuranceFields({ form, index }: { form: UseFormReturn<NewLoanValues>; index: number }) {
  const { t } = useTranslation();
  const e = form.formState.errors.insurances?.[index];
  const mode = form.watch(`insurances.${index}.mode`);
  return (
    <>
      <Field label={t('loan_form.insurance_mode')} error={e?.mode?.message}>
        {(c) => (
          <select {...c} {...form.register(`insurances.${index}.mode`)} className={input}>
            {INSURANCE_MODES.map((m) => <option key={m} value={m}>{t(`loan_form.insurance_mode_option.${m}`)}</option>)}
          </select>
        )}
      </Field>
      {mode === 'fixed_monthly' ? (
        <Field label={t('loan_form.insurance_amount')} error={e?.amount?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register(`insurances.${index}.amount`)} className={`w-32 ${input}`} />}
        </Field>
      ) : (
        <Field label={t('loan_form.insurance_rate')} error={e?.rate?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register(`insurances.${index}.rate`)} className={`w-24 ${input}`} />}
        </Field>
      )}
      <Field label={t('loan_form.starts_on')} error={e?.startsOn?.message}>
        {(c) => <input type="date" {...c} {...form.register(`insurances.${index}.startsOn`)} className={input} />}
      </Field>
      <Field label={t('loan_form.ends_on')} hint={t('loan_form.optional')} error={e?.endsOn?.message}>
        {(c) => <input type="date" {...c} {...form.register(`insurances.${index}.endsOn`)} className={input} />}
      </Field>
      <Field label={t('loan_form.insurance_note')} error={e?.note?.message}>
        {(c) => <textarea {...c} {...form.register(`insurances.${index}.note`)} className={`w-full ${input}`} />}
      </Field>
    </>
  );
}
