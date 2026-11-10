import { Controller, type FieldValues, type Path, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Folded } from '@/components/form/Folded';
import type { TermsValues } from './childcareSchema';
import { ABSENCE_REASONS, reasonLabel } from './statementText';
import '@/locales/childcare';

type TextField = Exclude<keyof TermsValues, 'weekTypes' | 'lunchProvider' | 'snackProvider' | 'unpaid'>;
type WithTerms = FieldValues & TermsValues;
type Message = { message?: string } | undefined;

/** The terms' figures, shared by the creation page and the edit and revision dialogs; `withFrom` asks for the start date. */
export function RateFields<T extends WithTerms>({ form, withFrom, fromLabel }: { form: UseFormReturn<T>; withFrom: boolean; fromLabel?: string }) {
  const { t } = useTranslation();
  const e = form.formState.errors as Record<TextField, Message> & { weekTypes?: { weeks?: Message }[] };
  const text = (name: TextField, label: string, mode: 'decimal' | 'numeric' = 'decimal', hint?: string) => (
    <Field label={label} hint={hint} error={e[name]?.message}>
      {(c) => <Input inputMode={mode} {...c} {...form.register(name as Path<T>)} />}
    </Field>
  );
  const provider = (name: 'lunchProvider' | 'snackProvider', label: string) => (
    <Controller control={form.control} name={name as Path<T>} render={({ field }) => (
      <Field label={label}>
        {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
          options={[{ value: 'nanny', label: t('childcare_form.provider_nanny') }, { value: 'employer', label: t('childcare_form.provider_employer') }]} />}
      </Field>
    )} />
  );
  return (
    <>
      {withFrom ? (
        <Controller control={form.control} name={'from' as Path<T>} render={({ field }) => (
          <Field label={fromLabel ?? t('childcare_form.from')} error={e.from?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
      ) : null}
      {text('net', t('childcare_form.net'))}
      {text('upkeep', t('childcare_form.upkeep'), 'decimal', t('childcare_form.upkeep_hint'))}
      {text('lunch', t('childcare_form.lunch_price'))}
      {provider('lunchProvider', t('childcare_form.lunch_provider'))}
      {text('snack', t('childcare_form.snack_price'))}
      {provider('snackProvider', t('childcare_form.snack_provider'))}
      {/* Several template weeks ask for their weeks with their days, in the schedule step. */}
      {form.watch('weekTypes' as Path<T>).length === 1 ? (
        <Field label={t('childcare_form.weeks')} hint={t('childcare_form.weeks_hint')} error={e.weekTypes?.[0]?.weeks?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register('weekTypes.0.weeks' as Path<T>)} />}
        </Field>
      ) : null}
      <Controller control={form.control} name={'unpaid' as Path<T>} render={({ field }) => (
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium">{t('childcare_form.unpaid_absences')}</legend>
          <p className="text-sm text-muted-foreground">{t('childcare_form.unpaid_absences_hint')}</p>
          {ABSENCE_REASONS.map((reason) => (
            <label key={reason} className="flex items-center gap-2 text-sm">
              <Checkbox checked={(field.value as string[]).includes(reason)}
                onCheckedChange={(on) => field.onChange(on === true ? [...(field.value as string[]), reason] : (field.value as string[]).filter((r) => r !== reason))} />
              {reasonLabel(t, reason)}
            </label>
          ))}
        </fieldset>
      )} />
      <Folded label={t('childcare_form.rates_more')} hasError={Boolean(e.gross ?? e.overtimePct)}>
        {text('overtimePct', t('childcare_form.overtime'))}
        {text('gross', t('childcare_form.gross'))}
      </Folded>
    </>
  );
}
