import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
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
import { Reload } from '@/components/form/Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { invalidateProperties, RECURRING_COST, RECURRING_COSTS, type PropertyDetail, type PropertyRecurringCost } from '@/api/properties';
import { EVERY_UNITS, RECURRING_CATEGORIES } from '../format';
import { COST_PATHS, costSchema, costValuesOf, toCostBody, type CostValues } from './cost';
import '@/locales/loans';
import '@/locales/forms';

/**
 * Spec 3D2 §4: a planned cost added (`editing` absent: POST) or corrected (PUT, with the version read) in a dialog. A save
 * hands the dialog back (`close()`) and the section announces and places the focus; « Annuler » and Escape call `onClose`.
 */
export function CostDialog({ property, editing, onClose, onDone }: {
  property: PropertyDetail; editing?: PropertyRecurringCost; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={editing ? t('property_costs.edit_title') : t('property_costs.add_title')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <CostForm property={property} editing={editing} titleId={titleId}
          onDone={(message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); }} />
      )}
    </ModalDialog>
  );
}

function CostForm({ property, editing, titleId, onDone }: { property: PropertyDetail; editing?: PropertyRecurringCost; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The version the form opened on: a refetch meanwhile must not turn the expected 409 into an overwrite.
  const [version] = useState(editing?.version);
  const form = useForm<CostValues>({ resolver: zodResolver(costSchema(t)), defaultValues: costValuesOf(editing) });
  const errors = useSubmitErrors(form.setError, COST_PATHS);
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
  const date = (name: 'startsOn' | 'endsOn', label: string, hint?: string) => (
    <Controller control={form.control} name={name} render={({ field }) => (
      <Field label={label} hint={hint} error={e[name]?.message}>
        {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
      </Field>
    )} />
  );

  return (
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row changed or went: reading it again closes the dialog onto what is now stored. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found'
            ? <Reload onReload={() => invalidateProperties(queryClient).then(() => onDone())} />
            : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="category" render={({ field }) => (
          <Field label={t('property_costs.category')} error={e.category?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={RECURRING_CATEGORIES.map((k) => ({ value: k, label: t(`property_flows.category_option.${k}`) }))} />}
          </Field>
        )} />
        <Field label={t('property_costs.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
        <Field label={t('wizard.every')} error={e.everyCount?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register('everyCount')} />}
        </Field>
        <Controller control={form.control} name="everyUnit" render={({ field }) => (
          <Field label={t('wizard.unit')} error={e.everyUnit?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={EVERY_UNITS.map((k) => ({ value: k, label: t(`property_costs.unit_option.${k}`) }))} />}
          </Field>
        )} />
        {date('startsOn', t('property_costs.starts'))}
        {date('endsOn', t('property_costs.ends'), t('loan_form.optional'))}
      </div>
      <Field label={t('property_costs.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('property_costs.submit')}</Button>
      </div>
    </form>
  );
}
