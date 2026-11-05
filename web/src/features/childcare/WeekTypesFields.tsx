import { useFieldArray, type FieldValues, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import type { TermsValues } from './childcareSchema';
import { ScheduleFields } from './ScheduleFields';
import '@/locales/childcare';

type Message = { message?: string } | undefined;
type WeekTypeErrors = { root?: Message; message?: string; [k: number]: { name?: Message; weeks?: Message } | undefined };

/**
 * The template weeks, one or several: a single one is just the weekdays (its weeks sit with the rates), from two on each shows
 * its name and weeks. A new one starts from the first one's days, so only what differs is edited.
 */
export function WeekTypesFields<T extends FieldValues & TermsValues>({ form }: { form: UseFormReturn<T> }) {
  const { t } = useTranslation();
  const f = form as unknown as UseFormReturn<TermsValues>;
  const { fields, append, remove } = useFieldArray({ control: f.control, name: 'weekTypes' });
  const errors = f.formState.errors.weekTypes as WeekTypeErrors | undefined;
  const many = fields.length > 1;
  const whole = errors?.root?.message ?? errors?.message;
  const total = f.watch('weekTypes').reduce((n, w) => n + (/^\d+$/.test(w.weeks.trim()) ? Number(w.weeks) : 0), 0);
  return (
    <div className="space-y-4">
      {whole ? <p role="alert" className="text-sm text-destructive">{whole}</p> : null}
      {fields.map((field, i) => (
        many ? (
          <fieldset key={field.id} aria-label={t('childcare_form.week_type_n', { n: i + 1 })} className="space-y-3 rounded-md border p-3">
            <div className="flex flex-wrap items-start gap-3">
              <Field label={t('childcare_form.week_type_name', { n: i + 1 })} error={errors?.[i]?.name?.message}>
                {(c) => <Input {...c} {...f.register(`weekTypes.${i}.name`)} />}
              </Field>
              <Field label={t('childcare_form.week_type_weeks', { n: i + 1 })} error={errors?.[i]?.weeks?.message}>
                {(c) => <Input inputMode="numeric" className="w-24" {...c} {...f.register(`weekTypes.${i}.weeks`)} />}
              </Field>
              <Button type="button" variant="ghost" onClick={() => remove(i)}>{t('childcare_form.week_type_remove', { n: i + 1 })}</Button>
            </div>
            <ScheduleFields form={form} name={`weekTypes.${i}.days`} />
          </fieldset>
        ) : <ScheduleFields key={field.id} form={form} name={`weekTypes.${i}.days`} />
      ))}
      <Button type="button" variant="outline" onClick={() => append({ name: '', weeks: '', days: structuredClone(f.getValues('weekTypes.0.days')) })}>
        {t('childcare_form.week_type_add')}
      </Button>
      {many ? <p className="text-sm text-muted-foreground">{t('childcare_form.weeks_total', { count: total })}</p> : null}
    </div>
  );
}
