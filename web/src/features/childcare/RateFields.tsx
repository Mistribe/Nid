import { Controller, type FieldValues, type Path, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Input } from '@/components/ui/input';
import { Folded } from '../loans/new/Folded';
import type { TermsValues } from './childcareSchema';
import '@/locales/childcare';

type WithTerms = FieldValues & TermsValues;
type Message = { message?: string } | undefined;

/** The terms' figures, shared by the creation page and the edit and revision dialogs; `withFrom` asks for the start date. */
export function RateFields<T extends WithTerms>({ form, withFrom, fromLabel }: { form: UseFormReturn<T>; withFrom: boolean; fromLabel?: string }) {
  const { t } = useTranslation();
  const e = form.formState.errors as Record<Exclude<keyof TermsValues, 'weekTypes'>, Message> & { weekTypes?: { weeks?: Message }[] };
  const text = (name: Exclude<keyof TermsValues, 'weekTypes'>, label: string, mode: 'decimal' | 'numeric' = 'decimal', hint?: string) => (
    <Field label={label} hint={hint} error={e[name]?.message}>
      {(c) => <Input inputMode={mode} {...c} {...form.register(name as Path<T>)} />}
    </Field>
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
      {text('meal', t('childcare_form.meal_price'))}
      {/* Several template weeks ask for their weeks with their days, in the schedule step. */}
      {form.watch('weekTypes' as Path<T>).length === 1 ? (
        <Field label={t('childcare_form.weeks')} hint={t('childcare_form.weeks_hint')} error={e.weekTypes?.[0]?.weeks?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register('weekTypes.0.weeks' as Path<T>)} />}
        </Field>
      ) : null}
      <Folded label={t('childcare_form.rates_more')} hasError={Boolean(e.gross ?? e.overtimePct)}>
        {text('overtimePct', t('childcare_form.overtime'))}
        {text('gross', t('childcare_form.gross'))}
      </Folded>
    </>
  );
}
