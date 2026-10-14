import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, type ChangeEvent } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentRefusal } from '../../loans/refusal';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { FLOW, FLOWS, invalidateProperties, type PropertyDetail, type PropertyFlow } from '../api';
import { categoriesOf, FLOW_KINDS } from '../format';
import { FLOW_PATHS, flowSchema, flowValuesOf, toFlowBody, type FlowValues } from './flow';

const input = 'rounded-md border px-3 py-2';

/**
 * 3D1 design §6: a flow added (`editing` absent: POST) or corrected inline on its row (PUT: the server supersedes it, so
 * the corrected row comes back under a new id). The kind comes first and the category list follows it. The section
 * announces and places the focus: this form is gone by then.
 */
export function FlowForm({ property, editing, onDone }: { property: PropertyDetail; editing?: PropertyFlow; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const headingId = useId();
  const form = useForm<FlowValues>({ resolver: zodResolver(flowSchema(t)), defaultValues: flowValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, FLOW_PATHS);
  // Mount only: opening the form lands the person in it (the kind when adding, the amount when correcting).
  useEffect(() => { form.setFocus(editing ? 'amount' : 'kind'); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const add = $api.useMutation('post', FLOWS);
  const correct = $api.useMutation('put', FLOW);
  const kind = form.watch('kind');
  // A new kind starts on its first category (precision 6): an income never leaves with « Travaux ».
  const changeKind = (ev: ChangeEvent<HTMLSelectElement>) => form.setValue('category', categoriesOf(ev.target.value as FlowValues['kind'])[0] ?? '');

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const callbacks = {
      onSuccess: () => {
        void invalidateProperties(queryClient);
        onDone(editing ? t('property_flows.corrected') : t('property_flows.added'));
      },
      onError: (error: unknown) => errors.onError(error),
    };
    if (editing) correct.mutate({ params: { path: { id: property.id, flowId: editing.id } }, body: toFlowBody(values) }, callbacks);
    else add.mutate({ params: { path: { id: property.id } }, body: toFlowBody(values) }, callbacks);
  });

  const e = form.formState.errors;
  const problem = errors.problem ? presentRefusal(t, editing ? correct.error : add.error) : null;

  return (
    <form aria-labelledby={headingId} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      <h3 id={headingId} className="font-semibold">{editing ? t('property_flows.edit_title') : t('property_flows.add_title')}</h3>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row was corrected or deleted elsewhere: reading again drops it, and the section closes this form onto the heading. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found'
            ? <Reload onReload={() => invalidateProperties(queryClient)} />
            : null}
        </>
      ) : null}
      <Field label={t('property_flows.kind')} error={e.kind?.message}>
        {(c) => (
          <select {...c} {...form.register('kind', { onChange: changeKind })} className={input}>
            {FLOW_KINDS.map((k) => <option key={k} value={k}>{t(`property_flows.kind_option.${k}`)}</option>)}
          </select>
        )}
      </Field>
      {/* The category select is keyed by the kind: it remounts on the new list, showing the category the form now holds. */}
      <Field label={t('property_flows.category')} error={e.category?.message}>
        {(c) => (
          <select key={kind} {...c} {...form.register('category')} className={input}>
            {categoriesOf(kind).map((k) => <option key={k} value={k}>{t(`property_flows.category_option.${k}`)}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('property_flows.amount')} error={e.amount?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={`w-32 ${input}`} />}
      </Field>
      <Field label={t('property_flows.date')} error={e.occurredOn?.message}>
        {(c) => <input type="date" max={todayIso()} {...c} {...form.register('occurredOn')} className={input} />}
      </Field>
      <Field label={t('property_flows.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('property_flows.submit')}</Button>
        <Button type="button" variant="ghost" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
      </div>
    </form>
  );
}
