import { Controller, get, type FieldValues, type Path, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { dayName, type DayRow } from './childcareSchema';
import '@/locales/childcare';

type Message = { message?: string } | undefined;
type DayErrors = { message?: string; root?: Message; [i: number]: { arrival?: Message; departure?: Message } };

/**
 * One template week (`name` is the path of its `days`, `weekTypes.${i}.days`), one row per weekday: « accueilli », arrival, departure, meal. The times are `Input type="time"`:
 * shadcn/ui has no time picker.
 */
export function ScheduleFields<T extends FieldValues>({ form, name: base }: { form: UseFormReturn<T>; name: string }) {
  const { t } = useTranslation();
  const days = form.watch(base as Path<T>) as DayRow[];
  const errors = get<unknown>(form.formState.errors, base) as DayErrors | undefined;
  const path = (i: number, field: keyof DayRow) => `${base}.${i}.${field}` as Path<T>;
  const whole = errors?.message ?? errors?.root?.message;
  return (
    <fieldset className="space-y-2">
      <legend className="text-sm">{t('childcare_form.schedule_legend')}</legend>
      {whole ? <p className="text-sm text-destructive">{whole}</p> : null}
      <ul className="space-y-2">
        {days.map((d, i) => {
          const name = dayName(t, d.day);
          const rowErrors = errors?.[i];
          return (
            <li key={d.day} className="flex flex-wrap items-center gap-3">
              <label className="flex w-32 items-center gap-2 text-sm">
                <Controller control={form.control} name={path(i, 'on')} render={({ field }) => (
                  <Checkbox checked={field.value} onCheckedChange={(v) => field.onChange(v === true)} aria-label={name} />
                )} />
                <span aria-hidden="true">{name}</span>
              </label>
              {d.on ? (
                <>
                  <Input type="time" className="w-28" aria-label={t('childcare_form.arrival_of', { day: name })} aria-invalid={rowErrors?.arrival ? true : undefined} {...form.register(path(i, 'arrival'))} />
                  <Input type="time" className="w-28" aria-label={t('childcare_form.departure_of', { day: name })} aria-invalid={rowErrors?.departure ? true : undefined} {...form.register(path(i, 'departure'))} />
                  <label className="flex items-center gap-2 text-sm">
                    <Controller control={form.control} name={path(i, 'meal')} render={({ field }) => (
                      <Checkbox checked={field.value} onCheckedChange={(v) => field.onChange(v === true)} aria-label={t('childcare_form.meal_of', { day: name })} />
                    )} />
                    <span aria-hidden="true">{t('childcare_form.meal')}</span>
                  </label>
                  {rowErrors?.arrival?.message ?? rowErrors?.departure?.message
                    ? <span className="basis-full text-sm text-destructive">{rowErrors?.arrival?.message ?? rowErrors?.departure?.message}</span> : null}
                </>
              ) : null}
            </li>
          );
        })}
      </ul>
    </fieldset>
  );
}
