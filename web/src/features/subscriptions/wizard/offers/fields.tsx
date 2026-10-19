import { useEffect, useId } from 'react';
import { Controller, type Path, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { CurrencyField } from '@/components/form/CurrencyField';
import { DateField as DateInput } from '@/components/form/DateField';
import { Field, type FieldControl } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField, type Option } from '@/components/form/SelectField';
import { Input } from '@/components/ui/input';
import type { WizardValues } from '../schema';

export type Person = components['schemas']['PersonDto'];
export type Form = UseFormReturn<WizardValues>;
export const UNITS = ['day', 'week', 'month', 'year'] as const;

type Props = { form: Form; name: Path<WizardValues>; label: string; hint?: string };

const errorOf = (form: Form, name: Path<WizardValues>) => form.getFieldState(name, form.formState).error?.message;

export function DateField({ form, name, label, hint }: Props) {
  return (
    <Controller control={form.control} name={name} render={({ field }) => (
      <Field label={label} hint={hint} error={errorOf(form, name)}>
        {(c) => <DateInput {...c} value={field.value as string} onChange={field.onChange} />}
      </Field>
    )} />
  );
}

/** The subscription's one currency (the same `currency` the phases use), as a searchable list. */
export function CurrencyInput({ form }: { form: Form }) {
  const { t } = useTranslation();
  return (
    <Controller control={form.control} name="currency" render={({ field }) => (
      <Field label={t('wizard.currency')} error={errorOf(form, 'currency')}>
        {(c) => <CurrencyField {...c} value={field.value} onValueChange={field.onChange} />}
      </Field>
    )} />
  );
}

/**
 * An amount; with `withCurrency`, followed by the subscription's one currency (the same `currency` the phases use),
 * so an offer without a following phase is not silently recorded in the default one.
 */
export function AmountField({ form, name, label, hint, withCurrency = false }: Props & { withCurrency?: boolean }) {
  const amount = (
    <Field label={label} hint={hint} error={errorOf(form, name)}>
      {(c) => <Input inputMode="decimal" {...c} {...form.register(name)} />}
    </Field>
  );
  if (!withCurrency) return amount;
  return <div className="grid gap-3 sm:grid-cols-2">{amount}<CurrencyInput form={form} /></div>;
}

/** A list of choices as a segmented control, grouped under `label`. */
export function Choice({ label, value, onValueChange, options }: {
  label: string; value: string; onValueChange: (v: string) => void; options: readonly Option[];
}) {
  const id = useId();
  return (
    <fieldset className="space-y-1">
      <legend id={id} className="text-sm">{label}</legend>
      <Segmented aria-labelledby={id} value={value} onValueChange={onValueChange} options={options} />
    </fieldset>
  );
}

/** A unit: named by its `Field` (`control`), or by `label` alone inside a group. */
export function UnitSelect({ form, name, control, label }: { form: Form; name: Path<WizardValues>; control?: FieldControl; label?: string }) {
  const { t } = useTranslation();
  return (
    <Controller control={form.control} name={name} render={({ field }) => (
      // `aria-label` is not part of FieldControl: a spread carries it to the trigger.
      <SelectField {...(control ?? { id: `${name}-unit` })} {...(label ? { 'aria-label': label } : {})} value={field.value as string}
        onValueChange={field.onChange} options={UNITS.map((u) => ({ value: u, label: t(`edit.unit.${u}`) }))} />
    )} />
  );
}

/** A count and its unit, grouped under `label`, which also names the count. */
export function DurationFields({ form, count, unit, label, hint }: {
  form: Form; count: Path<WizardValues>; unit: Path<WizardValues>; label: string; hint?: string;
}) {
  const { t } = useTranslation();
  const legendId = useId();
  const hintId = useId();
  const errorId = useId();
  const error = errorOf(form, count);
  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ') || undefined;
  return (
    <fieldset className="space-y-1">
      <legend id={legendId} className="text-sm">{label}</legend>
      <div className="grid gap-2 sm:grid-cols-2">
        <Input inputMode="numeric" aria-labelledby={legendId} aria-describedby={describedBy} aria-invalid={error ? true : undefined}
          {...form.register(count)} />
        <UnitSelect form={form} name={unit} label={t('wizard.unit')} />
      </div>
      {hint ? <span id={hintId} className="block text-sm text-muted-foreground">{hint}</span> : null}
      {error ? <span id={errorId} className="block text-sm text-destructive">{error}</span> : null}
    </fieldset>
  );
}

/** « Qui paie »: one of the household's persons. */
export function PersonSelect({ form, name, persons }: { form: Form; name: Path<WizardValues>; persons: Person[] }) {
  const { t } = useTranslation();
  return (
    <Controller control={form.control} name={name} render={({ field }) => (
      <Field label={t('wizard.payer_who')} error={errorOf(form, name)}>
        {(c) => <SelectField {...c} value={field.value as string} onValueChange={field.onChange}
          options={[{ value: '', label: t('wizard.none') }, ...persons.map((p) => ({ value: p.id, label: p.displayName }))]} />}
      </Field>
    )} />
  );
}

/** Who paid the offer: the household, one of its persons, or (when allowed) someone outside it. */
export function PayerFields({ form, persons, withExternal = false }: { form: Form; persons: Person[]; withExternal?: boolean }) {
  const { t } = useTranslation();
  const types = withExternal ? (['household', 'person', 'external'] as const) : (['household', 'person'] as const);
  const label = { household: t('wizard.payer_household'), person: t('wizard.payer_person'), external: t('wizard.offer.payer_external') };
  const type = form.watch('offerPayerType');
  return (
    <>
      <Choice label={t('wizard.payer')} value={type} options={types.map((p) => ({ value: p, label: label[p] }))}
        onValueChange={(v) => form.setValue('offerPayerType', v as typeof type)} />
      {type === 'person' ? <PersonSelect form={form} name="offerPayerPerson" persons={persons} /> : null}
    </>
  );
}

/** The purchase label, prefilled once with `initial` when the field is still empty. */
export function LabelField({ form, initial }: { form: Form; initial: string }) {
  const { t } = useTranslation();
  useEffect(() => {
    if (form.getValues('offerLabel').trim() === '') form.setValue('offerLabel', initial);
  }, [form, initial]);
  return (
    <Field label={t('wizard.offer.label')} error={errorOf(form, 'offerLabel')}>
      {(c) => <Input {...c} {...form.register('offerLabel')} />}
    </Field>
  );
}
