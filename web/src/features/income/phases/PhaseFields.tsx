import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import type { FieldValues, Path, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Field } from '../../subscriptions/edits/Field';
import { CADENCES, type PhaseValues } from './phase';

const input = 'rounded-md border px-3 py-2';

export type PhaseErrors = Partial<Record<keyof PhaseValues, { message?: string }>>;

/**
 * One phase's fields under `prefix` (`phases.0.` for a line of the creation, `` for the add/correct form). RHF types
 * paths against the concrete form, so the names are cast to `Path<T>` as `OwnershipFields` does. `fields` keeps only some
 * of them (the change of amount shows the amounts, the cadence and the note).
 */
export function PhaseFields<T extends FieldValues>({ form, prefix, errors, fields }: {
  form: UseFormReturn<T>; prefix: '' | `phases.${number}.`; errors?: PhaseErrors; fields?: readonly (keyof PhaseValues)[];
}) {
  const { t } = useTranslation();
  const name = (field: keyof PhaseValues) => `${prefix}${field}` as Path<T>;
  const cadence = form.watch(name('cadence'));
  const shown = (field: keyof PhaseValues) => !fields || fields.includes(field);
  return (
    <>
      {shown('label') ? (
        <Field label={t('income_form.label')} error={errors?.label?.message}>
          {(c) => <input {...c} {...form.register(name('label'))} className="w-full" />}
        </Field>
      ) : null}
      {shown('netAmount') ? (
        <Field label={t('income_form.net_amount')} error={errors?.netAmount?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register(name('netAmount'))} className={`w-32 ${input}`} />}
        </Field>
      ) : null}
      {shown('cadence') ? (
        <Field label={t('income_form.cadence')} error={errors?.cadence?.message}>
          {(c) => (
            <NativeSelect {...c} {...form.register(name('cadence'))}>
              {CADENCES.map((k) => <NativeSelectOption key={k} value={k}>{t(`income_form.cadence_option.${k}`)}</NativeSelectOption>)}
            </NativeSelect>
          )}
        </Field>
      ) : null}
      {shown('cadence') && cadence === 'months' ? (
        <Field label={t('income_form.months')} error={errors?.months?.message}>
          {(c) => <input inputMode="numeric" {...c} {...form.register(name('months'))} className={`w-20 ${input}`} />}
        </Field>
      ) : null}
      {shown('startsOn') ? (
        <Field label={t('income_form.starts_on')} error={errors?.startsOn?.message}>
          {(c) => <input type="date" {...c} {...form.register(name('startsOn'))} />}
        </Field>
      ) : null}
      {shown('endsOn') ? (
        <Field label={t('income_form.ends_on')} hint={t('income_form.optional')} error={errors?.endsOn?.message}>
          {(c) => <input type="date" {...c} {...form.register(name('endsOn'))} />}
        </Field>
      ) : null}
      {shown('grossAmount') ? (
        <Field label={t('income_form.gross_amount')} hint={t('income_form.optional')} error={errors?.grossAmount?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register(name('grossAmount'))} className={`w-32 ${input}`} />}
        </Field>
      ) : null}
      {shown('maxCycles') ? (
        <Field label={t('income_form.max_cycles')} hint={t('income_form.optional')} error={errors?.maxCycles?.message}>
          {(c) => <input inputMode="numeric" {...c} {...form.register(name('maxCycles'))} className={`w-24 ${input}`} />}
        </Field>
      ) : null}
      {shown('note') ? (
        <Field label={t('income_form.phase_note')} error={errors?.note?.message}>
          {(c) => <textarea {...c} {...form.register(name('note'))} className="w-full" />}
        </Field>
      ) : null}
    </>
  );
}
