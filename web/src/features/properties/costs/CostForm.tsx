import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentRefusal } from '../../loans/refusal';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { invalidateProperties, RECURRING_COST, RECURRING_COSTS, type PropertyDetail, type PropertyRecurringCost } from '../api';
import { EVERY_UNITS, RECURRING_CATEGORIES } from '../format';
import { COST_PATHS, costSchema, costValuesOf, toCostBody, type CostValues } from './cost';

const input = 'rounded-md border px-3 py-2';

/**
 * Spec 3D2 §4: a planned cost added (`editing` absent: POST) or corrected inline on its row (PUT, with the version read).
 * The section announces and places the focus: this form is gone by then.
 */
export function CostForm({ property, editing, onDone }: { property: PropertyDetail; editing?: PropertyRecurringCost; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const headingId = useId();
  // The version the form opened on: a refetch meanwhile must not turn the expected 409 into an overwrite.
  const [version] = useState(editing?.version);
  const form = useForm<CostValues>({ resolver: zodResolver(costSchema(t)), defaultValues: costValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, COST_PATHS);
  // Mount only: opening the form lands the person in it (the category when adding, the amount when correcting).
  useEffect(() => { form.setFocus(editing ? 'amount' : 'category'); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const add = $api.useMutation('post', RECURRING_COSTS);
  const correct = $api.useMutation('put', RECURRING_COST);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const callbacks = {
      onSuccess: () => {
        void invalidateProperties(queryClient);
        onDone(editing ? t('property_costs.corrected') : t('property_costs.added'));
      },
      onError: (error: unknown) => errors.onError(error),
    };
    if (editing) correct.mutate({ params: { path: { id: property.id, costId: editing.id } }, body: toCostBody(values, version) }, callbacks);
    else add.mutate({ params: { path: { id: property.id } }, body: toCostBody(values) }, callbacks);
  });

  const e = form.formState.errors;
  const problem = errors.problem ? presentRefusal(t, editing ? correct.error : add.error) : null;

  return (
    <form aria-labelledby={headingId} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      <h3 id={headingId} className="font-semibold">{editing ? t('property_costs.edit_title') : t('property_costs.add_title')}</h3>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row changed or went: reading it again closes the form onto what is now stored. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found'
            ? <Reload onReload={() => invalidateProperties(queryClient).then(() => onDone())} />
            : null}
        </>
      ) : null}
      <Field label={t('property_costs.category')} error={e.category?.message}>
        {(c) => (
          <select {...c} {...form.register('category')} className={input}>
            {RECURRING_CATEGORIES.map((k) => <option key={k} value={k}>{t(`property_flows.category_option.${k}`)}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('property_costs.amount')} error={e.amount?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={`w-32 ${input}`} />}
      </Field>
      <Field label={t('wizard.every')} error={e.everyCount?.message}>
        {(c) => <input inputMode="numeric" {...c} {...form.register('everyCount')} className={`w-20 ${input}`} />}
      </Field>
      <Field label={t('wizard.unit')} error={e.everyUnit?.message}>
        {(c) => (
          <select {...c} {...form.register('everyUnit')} className={input}>
            {EVERY_UNITS.map((k) => <option key={k} value={k}>{t(`property_costs.unit_option.${k}`)}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('property_costs.starts')} error={e.startsOn?.message}>
        {(c) => <input type="date" {...c} {...form.register('startsOn')} className={input} />}
      </Field>
      <Field label={t('property_costs.ends')} hint={t('loan_form.optional')} error={e.endsOn?.message}>
        {(c) => <input type="date" {...c} {...form.register('endsOn')} className={input} />}
      </Field>
      <Field label={t('property_costs.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('property_costs.submit')}</Button>
        <Button type="button" variant="ghost" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
      </div>
    </form>
  );
}
