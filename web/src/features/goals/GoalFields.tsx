import { Controller, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Folded } from '../loans/new/Folded';
import { kindText } from './format';
import { KINDS, type GoalValues } from './goalSchema';
import '@/locales/goals';

/** The goal's own fields, shared by the creation page and the edit dialog; the kind is chosen on creation only. */
export function GoalFields({ form, withKind }: { form: UseFormReturn<GoalValues>; withKind: boolean }) {
  const { t } = useTranslation();
  const e = form.formState.errors;
  const kind = form.watch('kind');
  return (
    <>
      {withKind ? (
        <div className="space-y-1">
          <span id="goal-kind" className="block text-sm">{t('goals_form.kind')}</span>
          <Controller control={form.control} name="kind" render={({ field }) => (
            <Segmented aria-labelledby="goal-kind" value={field.value} onValueChange={field.onChange}
              options={KINDS.map((k) => ({ value: k, label: kindText(t, k) }))} />
          )} />
        </div>
      ) : null}
      <Field label={t('goals_form.name')} error={e.name?.message}>
        {(c) => <Input {...c} {...form.register('name')} />}
      </Field>
      {kind === 'savings' ? (
        <Field label={t('goals_form.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
      ) : (
        <Field label={t('goals_form.months')} hint={t('goals_form.months_hint')} error={e.months?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register('months')} />}
        </Field>
      )}
      <Controller control={form.control} name="targetDate" render={({ field }) => (
        <Field label={t('goals_form.target_date')} error={e.targetDate?.message}>
          {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
        </Field>
      )} />
      <Folded label={t('goals_form.more')} hasError={Boolean(e.note)}>
        <Field label={t('goals_form.note')} error={e.note?.message}>
          {(c) => <Textarea {...c} {...form.register('note')} />}
        </Field>
      </Folded>
    </>
  );
}
