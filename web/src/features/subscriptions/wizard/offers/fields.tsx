import { useEffect, useId } from 'react';
import type { Path, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { Field } from '../../edits/Field';
import type { WizardValues } from '../schema';

export type Person = components['schemas']['PersonDto'];
export type Form = UseFormReturn<WizardValues>;
export const UNITS = ['day', 'week', 'month', 'year'] as const;
export const input = 'rounded-md border px-3 py-2';

type Props = { form: Form; name: Path<WizardValues>; label: string; hint?: string };

const errorOf = (form: Form, name: Path<WizardValues>) => form.getFieldState(name, form.formState).error?.message;

export function DateField({ form, name, label, hint }: Props) {
  return (
    <Field label={label} hint={hint} error={errorOf(form, name)}>
      {(c) => <input type="date" {...c} {...form.register(name)} className={input} />}
    </Field>
  );
}

/**
 * An amount; with `withCurrency`, followed by the subscription's one currency (the same `currency` the phases use),
 * so an offer without a following phase is not silently recorded in the default one.
 */
export function AmountField({ form, name, label, hint, withCurrency = false }: Props & { withCurrency?: boolean }) {
  const { t } = useTranslation();
  const amount = (
    <Field label={label} hint={hint} error={errorOf(form, name)}>
      {(c) => <input inputMode="decimal" {...c} {...form.register(name)} className={`w-32 ${input}`} />}
    </Field>
  );
  if (!withCurrency) return amount;
  return (
    <div className="flex gap-3">
      {amount}
      <Field label={t('wizard.currency')} error={errorOf(form, 'currency')}>
        {(c) => <input maxLength={3} {...c} {...form.register('currency')} className={`w-20 uppercase ${input}`} />}
      </Field>
    </div>
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
      <div className="flex gap-2">
        <input inputMode="numeric" aria-labelledby={legendId} aria-describedby={describedBy} aria-invalid={error ? true : undefined}
          {...form.register(count)} className={`w-20 ${input}`} />
        <select aria-label={t('wizard.unit')} {...form.register(unit)} className={input}>
          {UNITS.map((u) => <option key={u} value={u}>{t(`edit.unit.${u}`)}</option>)}
        </select>
      </div>
      {hint ? <span id={hintId} className="block text-sm text-muted-foreground">{hint}</span> : null}
      {error ? <span id={errorId} className="block text-sm text-destructive">{error}</span> : null}
    </fieldset>
  );
}

/** Who paid the offer: the household, one of its persons, or (when allowed) someone outside it. */
export function PayerFields({ form, persons, withExternal = false }: { form: Form; persons: Person[]; withExternal?: boolean }) {
  const { t } = useTranslation();
  const types = withExternal ? (['household', 'person', 'external'] as const) : (['household', 'person'] as const);
  const label = { household: t('wizard.payer_household'), person: t('wizard.payer_person'), external: t('wizard.offer.payer_external') };
  return (
    <>
      <fieldset className="space-y-1">
        <legend className="text-sm">{t('wizard.payer')}</legend>
        {types.map((p) => (
          <label key={p} className="flex items-center gap-2"><input type="radio" value={p} {...form.register('offerPayerType')} />{label[p]}</label>
        ))}
      </fieldset>
      {form.watch('offerPayerType') === 'person' ? (
        <Field label={t('wizard.payer_who')} error={errorOf(form, 'offerPayerPerson')}>
          {(c) => (
            <select {...c} {...form.register('offerPayerPerson')} className={input}>
              <option value="">{t('wizard.none')}</option>
              {persons.map((p) => <option key={p.id} value={p.id}>{p.displayName}</option>)}
            </select>
          )}
        </Field>
      ) : null}
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
      {(c) => <input {...c} {...form.register('offerLabel')} className={`w-full ${input}`} />}
    </Field>
  );
}
