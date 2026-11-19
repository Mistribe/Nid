import { Controller, useFieldArray, type ArrayPath, type FieldValues, type Path, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import type { GapRow } from './childcareSchema';
import '@/locales/childcare';

type WithGaps = FieldValues & { gaps: GapRow[] };

/** The weeks without care: each row a Monday and a number of weeks; the server checks them against the programmed weeks. */
export function GapFields<T extends WithGaps>({ form }: { form: UseFormReturn<T> }) {
  const { t } = useTranslation();
  const rows = useFieldArray({ control: form.control, name: 'gaps' as ArrayPath<T> });
  const errors = form.formState.errors.gaps as { message?: string; [i: number]: { monday?: { message?: string }; weeks?: { message?: string } } } | undefined;
  // A full year has none: its leave goes in the months' days (the hook above runs either way).
  if (form.watch('fullYear' as Path<T>) === true) return <p className="text-sm text-muted-foreground">{t('childcare_form.gaps_full_year')}</p>;
  return (
    <div className="space-y-3">
      <p className="text-sm text-muted-foreground">{t('childcare_form.gaps_hint')}</p>
      {errors?.message ? <p className="text-sm text-destructive">{errors.message}</p> : null}
      {rows.fields.map((row, i) => (
        <div key={row.id} className="flex flex-wrap items-end gap-3">
          <Controller control={form.control} name={`gaps.${i}.monday` as Path<T>} render={({ field }) => (
            <Field label={t('childcare_form.gap_monday', { n: i + 1 })} error={errors?.[i]?.monday?.message}>
              {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
            </Field>
          )} />
          <Field label={t('childcare_form.gap_weeks', { n: i + 1 })} error={errors?.[i]?.weeks?.message}>
            {(c) => <Input inputMode="numeric" className="w-24" {...c} {...form.register(`gaps.${i}.weeks` as Path<T>)} />}
          </Field>
          <Button type="button" variant="ghost" onClick={() => rows.remove(i)}>{t('childcare_form.gap_remove')}</Button>
        </div>
      ))}
      <Button type="button" variant="outline" onClick={() => rows.append({ monday: '', weeks: '1' } as never)}>{t('childcare_form.gap_add')}</Button>
    </div>
  );
}
