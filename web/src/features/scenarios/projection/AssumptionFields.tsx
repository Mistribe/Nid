import { useId } from 'react';
import { Controller, useFormContext, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { formatCurrency } from '@/components/Money';
import { Input } from '@/components/ui/input';
import type { Projection } from '@/features/dashboard/api';
import { CLASSES, useClassName } from '@/features/dashboard/panels/allocationNames';
import { toWireAmount } from '../../subscriptions/edits/amount';
import type { ProjectionOverrides } from './api';
import '@/locales/forms';
import '@/locales/scenarios';

/** The assumptions a scenario changes, as typed (spec §2.1 and §4): an empty field keeps the household's. */
export type AssumptionsDraft = { classRates: Record<string, string>; inflation: string; monthlySavings: string; savingsClass: string; freed: string };

/** The part of a form these fields fill: its horizon and its assumptions. */
type HorizonAndAssumptions = { horizon: string; assumptions: AssumptionsDraft };

export const YEARS = ['5y', '10y', '20y', '30y'] as const;

/** A rate as typed (`3,5`, `-1`); a signed amount (`-200`, `1 200,50`). */
const RATE = /^-?\d+([.,]\d+)?$/;
const SIGNED = /^-?\d+([.,]\d{1,4})?$/;
const bare = (text: string) => text.replace(/\s/g, '');
/** A server percent or amount as a French one: « 6.0 » reads « 6,0 ». */
export const comma = (value: string) => value.replace('.', ',');

/** The draft of saved overrides (each server string with a French comma), or a blank one. */
export function assumptionsDraft(o?: ProjectionOverrides | null): AssumptionsDraft {
  return {
    classRates: Object.fromEntries(CLASSES.map((c) => [c, o?.classRates?.[c] == null ? '' : comma(o.classRates[c])])),
    inflation: o?.inflation == null ? '' : comma(o.inflation),
    monthlySavings: o?.monthlySavings == null ? '' : comma(o.monthlySavings),
    savingsClass: o?.savingsClass ?? '',
    freed: o?.freedPaymentsToSavings == null ? '' : o.freedPaymentsToSavings ? 'yes' : 'no',
  };
}

/** The assumptions the scenario changes, each typed field as the wire reads it; none at all is `null` (the household's). */
export function overridesOf(a: AssumptionsDraft): ProjectionOverrides | null {
  const rates = Object.fromEntries(Object.entries(a.classRates).filter(([, v]) => bare(v)).map(([k, v]) => [k, toWireAmount(v)]));
  const o: ProjectionOverrides = {
    ...(Object.keys(rates).length > 0 ? { classRates: rates } : {}),
    ...(bare(a.inflation) ? { inflation: toWireAmount(a.inflation) } : {}),
    ...(bare(a.monthlySavings) ? { monthlySavings: toWireAmount(a.monthlySavings) } : {}),
    ...(a.savingsClass ? { savingsClass: a.savingsClass } : {}),
    ...(a.freed ? { freedPaymentsToSavings: a.freed === 'yes' } : {}),
  };
  return Object.keys(o).length > 0 ? o : null;
}

/** Every typed assumption reads as a number: the document can be simulated. */
export const isComplete = (a: AssumptionsDraft) =>
  [...Object.values(a.classRates), a.inflation].every((v) => !bare(v) || RATE.test(bare(v))) && (!bare(a.monthlySavings) || SIGNED.test(bare(a.monthlySavings)));

/** A server path of the horizon or the assumptions (`horizon`, `assumptions.classRates.equity`) as the draft's field. */
export function assumptionPath(path: string): 'horizon' | `assumptions.${string}` | undefined {
  if (path === 'horizon') return path;
  if (path === 'assumptions.freedPaymentsToSavings') return 'assumptions.freed';
  if (/^assumptions\.(classRates\.[a-z_]+|inflation|monthlySavings|savingsClass)$/.test(path)) return path as `assumptions.${string}`;
  return undefined;
}

/** The horizon: 5 to 30 years, or a retirement the server accepts as one (spec §2.3). */
export function HorizonField({ base }: { base: Projection }) {
  const { t } = useTranslation();
  const form = useFormContext<HorizonAndAssumptions>();
  const horizonId = useId();
  return (
    <div className="space-y-1">
      <span id={horizonId} className="block text-sm">{t('scenarios.projection.form.horizon')}</span>
      <Controller control={form.control} name="horizon" render={({ field, fieldState }) => (
        <>
          <Segmented aria-labelledby={horizonId} value={field.value} onValueChange={field.onChange}
            options={[
              ...YEARS.map((y) => ({ value: y, label: t('scenarios.projection.form.horizon_years', { count: Number.parseInt(y, 10) }) })),
              ...base.retirementHorizons.map((r) => ({ value: `retire:${r.person}`, label: t('scenarios.projection.form.retire', { name: r.name }) })),
            ]} />
          {fieldState.error ? <span className="block text-sm text-destructive">{fieldState.error.message}</span> : null}
        </>
      )} />
    </div>
  );
}

/** The assumption fields, each hinting the household's value it falls back to (`base` is the household's projection). */
export function AssumptionFields({ base }: { base: Projection }) {
  const { t } = useTranslation();
  const className = useClassName();
  const form = useFormContext<HorizonAndAssumptions>();
  const a = base.assumptions;
  const household = (value: string) => t('scenarios.projection.form.household_value', { value });
  const rate = (value: string | undefined) => household(value === undefined ? '' : `${comma(value)} %`);
  const choice = t('scenarios.projection.form.household_choice');
  return (
    <div className="grid gap-3 sm:grid-cols-3">
      <TextField name="assumptions.inflation" label={t('scenarios.projection.form.inflation')} hint={rate(a.inflation)} pattern={RATE} />
      <TextField name="assumptions.monthlySavings" label={t('scenarios.projection.form.monthly_savings')}
        hint={household(formatCurrency(base.monthlySavings, base.currency))} pattern={SIGNED} />
      <Controller control={form.control} name="assumptions.savingsClass" render={({ field, fieldState }) => (
        <Field label={t('scenarios.projection.form.savings_class')} error={fieldState.error?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
            options={[{ value: '', label: choice }, ...CLASSES.map((v) => ({ value: v, label: className(v) }))]} />}
        </Field>
      )} />
      <Controller control={form.control} name="assumptions.freed" render={({ field, fieldState }) => (
        <Field label={t('scenarios.projection.form.freed')} error={fieldState.error?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
            options={[
              { value: '', label: t('scenarios.projection.form.freed_household') },
              { value: 'yes', label: t('scenarios.projection.form.freed_yes') },
              { value: 'no', label: t('scenarios.projection.form.freed_no') },
            ]} />}
        </Field>
      )} />
      {CLASSES.map((c) => (
        <TextField key={c} name={`assumptions.classRates.${c}`} label={t('scenarios.projection.form.class_rate', { name: className(c) })}
          hint={rate(a.classRates[c])} pattern={RATE} />
      ))}
    </div>
  );
}

/** A figure that may stay empty (the household's); typed, it must read as `pattern`. */
function TextField({ name, label, hint, pattern }: { name: Path<HorizonAndAssumptions>; label: string; hint?: string; pattern: RegExp }) {
  const { t } = useTranslation();
  const form = useFormContext<HorizonAndAssumptions>();
  const error = form.getFieldState(name, form.formState).error?.message;
  return (
    <Field label={label} hint={hint} error={typeof error === 'string' ? error : undefined}>
      {(c) => <Input inputMode="decimal" {...c} {...form.register(name, {
        validate: (v) => { const typed = bare(typeof v === 'string' ? v : ''); return !typed || pattern.test(typed) || t('scenarios.projection.form.invalid_number'); },
      })} />}
    </Field>
  );
}
