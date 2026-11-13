import { Controller, type FieldValues, type Path, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { InfoTip } from '@/components/InfoTip';
import { SelectField } from '@/components/form/SelectField';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Folded } from '@/components/form/Folded';
import { FULL_YEAR_WEEKS, MAX_INCOMPLETE_WEEKS, type TermsValues } from './childcareSchema';
import { ABSENCE_REASONS, reasonLabel } from './statementText';
import '@/locales/childcare';

type TextField = Exclude<keyof TermsValues, 'weekTypes' | 'lunchProvider' | 'snackProvider' | 'unpaid' | 'fullYear'>;
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
      <YearField form={form} />
      {text('net', t('childcare_form.net'))}
      {text('upkeep', t('childcare_form.upkeep'), 'decimal', t('childcare_form.upkeep_hint'))}
      {text('lunch', t('childcare_form.lunch_price'))}
      {provider('lunchProvider', t('childcare_form.lunch_provider'))}
      {text('snack', t('childcare_form.snack_price'))}
      {provider('snackProvider', t('childcare_form.snack_provider'))}
      {/* Several template weeks ask for their weeks with their days, in the schedule step. */}
      {/* A full year's single template week covers the 52 weeks: nothing to ask. */}
      {form.watch('weekTypes' as Path<T>).length === 1 && !form.watch('fullYear' as Path<T>) ? (
        <Field label={t('childcare_form.weeks')} hint={t('childcare_form.weeks_hint')} error={e.weekTypes?.[0]?.weeks?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register('weekTypes.0.weeks' as Path<T>)} />}
        </Field>
      ) : null}
      <Controller control={form.control} name={'unpaid' as Path<T>} render={({ field }) => (
        <UnpaidAbsencesField value={field.value as string[]} onChange={field.onChange} />
      )} />
      <Folded label={t('childcare_form.rates_more')} hasError={Boolean(e.gross ?? e.overtimePct)}>
        {text('overtimePct', t('childcare_form.overtime'))}
        {text('gross', t('childcare_form.gross'))}
      </Folded>
    </>
  );
}

/**
 * Full year or incomplete year, asked as a plain question. Switching moves a single template week's weeks to the 52 a full
 * year takes, and back to 46 from them.
 */
function YearField<T extends WithTerms>({ form }: { form: UseFormReturn<T> }) {
  const { t } = useTranslation();
  const label = t('childcare_form.full_year');
  return (
    <Controller control={form.control} name={'fullYear' as Path<T>} render={({ field }) => (
      <Field label={label} hint={t('childcare_form.full_year_hint')}
        tip={<InfoTip label={label}>{t('childcare_form.full_year_tip')}</InfoTip>}>
        {(c) => (
          <Segmented aria-label={label} aria-describedby={c['aria-describedby']} value={field.value ? 'full' : 'incomplete'}
            options={[{ value: 'full', label: t('childcare_form.full_year_yes') }, { value: 'incomplete', label: t('childcare_form.full_year_no') }]}
            onValueChange={(v) => {
              const full = v === 'full';
              field.onChange(full);
              const types = form.getValues('weekTypes' as Path<T>) as TermsValues['weekTypes'];
              if (types.length !== 1) return;
              const weeks = types[0]!.weeks;
              if (full) form.setValue('weekTypes.0.weeks' as Path<T>, String(FULL_YEAR_WEEKS) as T[Path<T>]);
              else if (weeks === String(FULL_YEAR_WEEKS)) form.setValue('weekTypes.0.weeks' as Path<T>, String(MAX_INCOMPLETE_WEEKS) as T[Path<T>]);
            }} />
        )}
      </Field>
    )} />
  );
}

/** The absence reasons deducted from the salary, one checkbox each; also the past-revision correction (#192). */
export function UnpaidAbsencesField({ value, onChange }: { value: string[]; onChange: (value: string[]) => void }) {
  const { t } = useTranslation();
  return (
    <fieldset className="space-y-2">
      <legend className="text-sm font-medium">{t('childcare_form.unpaid_absences')}</legend>
      <p className="text-sm text-muted-foreground">{t('childcare_form.unpaid_absences_hint')}</p>
      {/* always deducted (Ipéria pays the training; the carer agreed to the family's), never a contract choice */}
      {ABSENCE_REASONS.filter((r) => r !== 'carer_training' && r !== 'family_agreed').map((reason) => (
        <label key={reason} className="flex items-center gap-2 text-sm">
          <Checkbox checked={value.includes(reason)} onCheckedChange={(on) => onChange(on === true ? [...value, reason] : value.filter((r) => r !== reason))} />
          {reasonLabel(t, reason)}
        </label>
      ))}
    </fieldset>
  );
}
