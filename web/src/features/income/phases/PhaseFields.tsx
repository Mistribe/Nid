import { Controller, type FieldValues, type Path, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Folded } from '../../loans/new/Folded';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { CADENCES, type PhaseValues } from './phase';
import '@/locales/income';
import '@/locales/loans';

export type PhaseErrors = Partial<Record<keyof PhaseValues, { message?: string }>>;

/**
 * One phase's fields under `prefix` (`phases.0.` for a line of the creation, `` for the add/correct form). RHF types
 * paths against the concrete form, so the names are cast to `Path<T>` as `OwnershipFields` does. `fields` keeps only some
 * of them (the change of amount shows the amounts, the cadence and the note).
 */
export function PhaseFields<T extends FieldValues>({ form, prefix, errors, fields, fold = false }: {
  form: UseFormReturn<T>; prefix: '' | `phases.${number}.`; errors?: PhaseErrors; fields?: readonly (keyof PhaseValues)[];
  /** Folds the rare fields (gross amount, number of payments, note) under « Plus d’options »: the creation's compact cards. */
  fold?: boolean;
}) {
  const { t } = useTranslation();
  const name = (field: keyof PhaseValues) => `${prefix}${field}` as Path<T>;
  const cadence = form.watch(name('cadence'));
  const shown = (field: keyof PhaseValues) => !fields || fields.includes(field);
  const date = (field: 'startsOn' | 'endsOn', label: string, hint?: string) => (
    <Controller control={form.control} name={name(field)} render={({ field: f }) => (
      <Field label={label} hint={hint} error={errors?.[field]?.message}>
        {(c) => <DateField {...c} value={String(f.value ?? '')} onChange={f.onChange} />}
      </Field>
    )} />
  );
  const gross = shown('grossAmount') ? (
    <Field label={t('income_form.gross_amount')} hint={t('income_form.gross_hint')} error={errors?.grossAmount?.message}>
      {(c) => <Input inputMode="decimal" {...c} {...form.register(name('grossAmount'))} />}
    </Field>
  ) : null;
  const taxable = shown('taxableAmount') ? (
    <Field label={t('income_form.taxable_amount')} hint={t('income_form.taxable_hint')} error={errors?.taxableAmount?.message}>
      {(c) => <Input inputMode="decimal" {...c} {...form.register(name('taxableAmount'))} />}
    </Field>
  ) : null;
  const maxCycles = shown('maxCycles') ? (
    <Field label={t('income_form.max_cycles')} hint={t('income_form.optional')} error={errors?.maxCycles?.message}>
      {(c) => <Input inputMode="numeric" {...c} {...form.register(name('maxCycles'))} />}
    </Field>
  ) : null;
  const note = shown('note') ? (
    <Field label={t('income_form.phase_note')} error={errors?.note?.message}>
      {(c) => <Textarea {...c} {...form.register(name('note'))} />}
    </Field>
  ) : null;
  return (
    <>
      <div className="grid gap-3 sm:grid-cols-3">
        {shown('label') ? (
          <Field label={t('income_form.label')} error={errors?.label?.message}>
            {(c) => <Input {...c} {...form.register(name('label'))} />}
          </Field>
        ) : null}
        {shown('netAmount') ? (
          <Field label={t('income_form.net_amount')} hint={t('income_form.net_hint')} error={errors?.netAmount?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register(name('netAmount'))} />}
          </Field>
        ) : null}
        {shown('cadence') ? (
          <Controller control={form.control} name={name('cadence')} render={({ field }) => (
            <Field label={t('income_form.cadence')} error={errors?.cadence?.message}>
              {(c) => (
                <SelectField {...c} value={String(field.value ?? '')} onValueChange={field.onChange}
                  options={CADENCES.map((k) => ({ value: k, label: t(`income_form.cadence_option.${k}`) }))} />
              )}
            </Field>
          )} />
        ) : null}
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        {shown('cadence') && cadence === 'months' ? (
          <Field label={t('income_form.months')} error={errors?.months?.message}>
            {(c) => <Input inputMode="numeric" {...c} {...form.register(name('months'))} />}
          </Field>
        ) : null}
        {shown('startsOn') ? date('startsOn', t('income_form.starts_on')) : null}
        {shown('endsOn') ? date('endsOn', t('income_form.ends_on'), t('income_form.optional')) : null}
        {fold ? null : gross}
        {fold ? null : taxable}
        {fold ? null : maxCycles}
      </div>
      {fold ? (
        <Folded label={t('loan_form.more_options')} hasError={Boolean(errors?.grossAmount || errors?.taxableAmount || errors?.maxCycles || errors?.note)}>
          <div className="grid gap-3 sm:grid-cols-2">{gross}{taxable}{maxCycles}</div>
          {note}
        </Folded>
      ) : note}
    </>
  );
}
