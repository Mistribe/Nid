import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '@/components/form/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { FLOW, FLOWS, invalidateInvestments, type EnvelopeDetail, type Flow } from '../api';
import { FLOW_KINDS } from '../format';
import { flowSchema, placeFieldError, presentConflict, trimAmount } from './entries';

const PATHS = { occurredOn: 'occurredOn', kind: 'kind', amount: 'amount', note: 'note' } as const;
const FALLBACK: Record<string, 'occurredOn' | 'amount'> = {
  'core.date_in_future': 'occurredOn', 'core.currency_mismatch': 'amount', 'core.invalid_fact': 'amount',
  'core.amount_not_positive': 'amount',
};

/**
 * The body of the « Ajouter un flux » / « Corriger le flux » dialog (2B2a design §6): a new flow posts `FLOWS`, a
 * correction puts `FLOW`. « Versements antérieurs » (`opening`) is offered only while the envelope has none yet,
 * unless this very form is correcting that one; `opening` starts a new flow on it (the missing-opening warning's
 * button). `onDone` is called with the confirmation message on save (announced and focused by the page, since a
 * correction gets a new id), or with no message when « Annuler » just abandons the form.
 */
export function FlowForm({ envelope, editing, opening, titleId, onDone }: {
  envelope: EnvelopeDetail; editing?: Flow; opening?: boolean; titleId: string; onDone: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const hasOpening = envelope.flows.some((f) => f.kind === 'opening');
  const kinds = FLOW_KINDS.filter((k) => k !== 'opening' || editing?.kind === 'opening' || !hasOpening);
  const form = useForm({
    resolver: zodResolver(flowSchema(t)),
    defaultValues: {
      occurredOn: editing?.occurredOn ?? todayIso(),
      kind: (editing?.kind as (typeof FLOW_KINDS)[number] | undefined) ?? (opening ? 'opening' : 'contribution'),
      amount: editing ? trimAmount(editing.amount.amount) : '',
      note: editing?.note ?? '',
    },
  });

  const errors = useSubmitErrors(form.setError, PATHS);
  const onFail = (error: unknown) => placeFieldError(error, t, form.setError, errors, FALLBACK);
  const create = $api.useMutation('post', FLOWS, {
    onSuccess: () => { errors.clear(); void invalidateInvestments(queryClient); onDone(t('envelope.flow_saved')); },
    onError: onFail,
  });
  const update = $api.useMutation('put', FLOW, {
    onSuccess: () => { errors.clear(); void invalidateInvestments(queryClient); onDone(t('envelope.flow_corrected')); },
    onError: onFail,
  });

  const submit = form.handleSubmit((values) => {
    const body = {
      occurredOn: values.occurredOn, kind: values.kind,
      amount: { amount: toWireAmount(values.amount), currency: envelope.currency },
      note: values.note || undefined,
      // A correction replaces the whole row (PUT): the category and external reference a value or an import gave
      // it (finding 8) travel unchanged — this form never lets a person set or clear them.
      ...(editing ? { category: editing.category, externalRef: editing.externalRef } : {}),
    };
    if (editing) update.mutate({ params: { path: { id: envelope.id, flowId: editing.id } }, body });
    else create.mutate({ params: { path: { id: envelope.id } }, body });
  });

  const e = form.formState.errors;
  const pending = editing ? update.isPending : create.isPending;
  // A conflict shows its own sentence when known (final findings #5), read from the mutation's own error rather
  // than `errors.problem` (which only carries the generic text `present()` gives every 409).
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, editing ? update.error : create.error) : errors.problem;

  return (
    <form aria-labelledby={titleId} onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3" noValidate>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="occurredOn" render={({ field }) => (
          <Field label={t('envelope.date')} error={e.occurredOn?.message}>
            {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
        <Controller control={form.control} name="kind" render={({ field }) => (
          <Field label={t('envelope.kind')} error={e.kind?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={kinds.map((k) => ({ value: k, label: t(`investments.flow_kind.${k}`) }))} />}
          </Field>
        )} />
        <Field label={t('envelope.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
      </div>
      <Field label={t('envelope.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('envelope.cancel')}</Button>
        <Button type="submit" disabled={pending}>{editing ? t('envelope.save') : t('envelope.add_flow')}</Button>
      </div>
    </form>
  );
}
