import { useSearch } from '@tanstack/react-router';
import { Controller, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import type { RootSearch } from '@/app/router';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Money } from '@/components/Money';
import { Segmented } from '@/components/form/Segmented';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Folded } from '../loans/new/Folded';
import { useEmergencyTarget } from './api';
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
      {withKind && kind === 'emergency_fund' ? <EmergencyTarget months={form.watch('months')} /> : null}
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

/** Issue #412: before saving, the target the server computes for these months (nothing while the months are not 1 to 24). */
function EmergencyTarget({ months }: { months: string }) {
  const { t } = useTranslation();
  const { scope }: RootSearch = useSearch({ strict: false });
  const valid = /^\d{1,2}$/.test(months.trim()) && Number(months) >= 1 && Number(months) <= 24;
  const answer = useEmergencyTarget(valid ? Number(months) : null, scope === 'me' ? 'me' : 'household');
  const target = valid ? answer.data : undefined;
  return (
    <p aria-live="polite" className="text-sm text-muted-foreground">
      {!target ? null : target.target
        ? <>{t('goals_form.computed_target')} <Money value={target.target} />{target.flags.includes('missing_currency') ? ` ${t('goals_form.computed_target_partial')}` : ''}</>
        : t('goals_list.no_commitments')}
    </p>
  );
}
