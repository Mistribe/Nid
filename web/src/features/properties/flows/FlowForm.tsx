import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { SelectField } from '@/components/form/SelectField';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { presentRefusal } from '../../loans/refusal';
import { Field } from '@/components/form/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { FLOW, FLOWS, invalidateProperties, type PropertyDetail, type PropertyFlow } from '../api';
import { categoriesOf, FLOW_KINDS } from '../format';
import { FLOW_PATHS, flowSchema, flowValuesOf, toFlowBody, type FlowValues } from './flow';

/**
 * 3D1 design §6: a flow added (`editing` absent: POST) or corrected (PUT: the server supersedes it, so the corrected row
 * comes back under a new id) in a dialog. The kind comes first and the category list follows it. A save hands the dialog
 * back (`close()`) and the section announces and places the focus; « Annuler » and Escape just call `onClose`.
 */
export function FlowDialog({ property, editing, onClose, onDone }: {
  property: PropertyDetail; editing?: PropertyFlow; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={editing ? t('property_flows.edit_title') : t('property_flows.add_title')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <FlowForm property={property} editing={editing} titleId={titleId}
          onDone={(message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); }} />
      )}
    </ModalDialog>
  );
}

function FlowForm({ property, editing, titleId, onDone }: { property: PropertyDetail; editing?: PropertyFlow; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<FlowValues>({ resolver: zodResolver(flowSchema(t)), defaultValues: flowValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, FLOW_PATHS);
  const add = $api.useMutation('post', FLOWS);
  const correct = $api.useMutation('put', FLOW);
  const kind = form.watch('kind');

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
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row was corrected or deleted elsewhere: reading again drops it, and the section closes this dialog onto the heading. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found'
            ? <Reload onReload={() => invalidateProperties(queryClient)} />
            : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="kind" render={({ field }) => (
          <Field label={t('property_flows.kind')} error={e.kind?.message}>
            {(c) => <SelectField {...c} value={field.value}
              // A new kind starts on its first category (precision 6): an income never leaves with « Travaux ».
              onValueChange={(v) => { field.onChange(v); form.setValue('category', categoriesOf(v as FlowValues['kind'])[0] ?? ''); }}
              options={FLOW_KINDS.map((k) => ({ value: k, label: t(`property_flows.kind_option.${k}`) }))} />}
          </Field>
        )} />
        <Controller control={form.control} name="category" render={({ field }) => (
          <Field label={t('property_flows.category')} error={e.category?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={categoriesOf(kind).map((k) => ({ value: k, label: t(`property_flows.category_option.${k}`) }))} />}
          </Field>
        )} />
        <Field label={t('property_flows.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
        <Controller control={form.control} name="occurredOn" render={({ field }) => (
          <Field label={t('property_flows.date')} error={e.occurredOn?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} max={todayIso()} />}
          </Field>
        )} />
      </div>
      <Field label={t('property_flows.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('property_flows.submit')}</Button>
      </div>
    </form>
  );
}
