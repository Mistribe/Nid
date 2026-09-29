import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { FLOW, FLOWS, invalidateInvestments, type EnvelopeDetail, type Flow } from '../api';
import { FLOW_KINDS } from '../format';
import { flowSchema, placeFieldError, presentConflict, trimAmount } from './entries';

const input = 'rounded-md border px-3 py-2';
const PATHS = { occurredOn: 'occurredOn', kind: 'kind', amount: 'amount', note: 'note' } as const;
const FALLBACK: Record<string, 'occurredOn' | 'amount'> = {
  'core.date_in_future': 'occurredOn', 'core.currency_mismatch': 'amount', 'core.invalid_fact': 'amount',
  'core.amount_not_positive': 'amount',
};

/**
 * The « Ajouter un flux » form at the top of the envelope's page (`editing` absent), or a correction inline on its
 * row (2B2a design §6): a new flow posts `FLOWS`, a correction puts `FLOW`. « Versements antérieurs » (`opening`)
 * is offered only while the envelope has none yet, unless this very form is correcting that one. `openingRequest`
 * is a counter the page bumps from the missing-opening warning's button: every change (even to the same request
 * again) sets the kind to `opening` and focuses it, so a second click always jumps back to it even if the person
 * had since picked something else. The row form closes on save: `onDone` is called with its confirmation message
 * (announced and focused by the page, since a correction gets a new id and this very component is about to
 * unmount), or with no message when « Annuler » just abandons the edit.
 */
export function FlowForm({ envelope, editing, openingRequest, onDone }: {
  envelope: EnvelopeDetail; editing?: Flow; openingRequest?: number; onDone?: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [saved, setSaved] = useState(false);
  const headingId = useId();
  const hasOpening = envelope.flows.some((f) => f.kind === 'opening');
  const kinds = FLOW_KINDS.filter((k) => k !== 'opening' || editing?.kind === 'opening' || !hasOpening);
  const form = useForm({
    resolver: zodResolver(flowSchema(t)),
    defaultValues: {
      occurredOn: editing?.occurredOn ?? todayIso(),
      kind: (editing?.kind as (typeof FLOW_KINDS)[number] | undefined) ?? 'contribution',
      amount: editing ? trimAmount(editing.amount.amount) : '',
      note: editing?.note ?? '',
    },
  });
  useEffect(() => {
    if (openingRequest !== undefined && !editing) { form.setValue('kind', 'opening'); form.setFocus('kind'); }
    // Reacts only to the page bumping `openingRequest`; `form` and `editing` are stable here.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openingRequest]);

  const errors = useSubmitErrors(form.setError, PATHS);
  const onFail = (error: unknown) => { setSaved(false); placeFieldError(error, t, form.setError, errors, FALLBACK); };
  const create = $api.useMutation('post', FLOWS, {
    onSuccess: () => {
      setSaved(true);
      errors.clear();
      void invalidateInvestments(queryClient);
      // Back to « Versement » (spec's default), whatever kind was just added — including a just-saved opening.
      form.reset({ occurredOn: todayIso(), kind: 'contribution', amount: '', note: '' });
    },
    onError: onFail,
  });
  const update = $api.useMutation('put', FLOW, {
    onSuccess: () => { errors.clear(); void invalidateInvestments(queryClient); onDone?.(t('envelope.flow_corrected')); },
    onError: onFail,
  });

  const submit = form.handleSubmit((values) => {
    setSaved(false);
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
  const heading = editing ? t('envelope.edit_flow') : t('envelope.new_flow');
  // A conflict shows its own sentence when known (final findings #5), read from the mutation's own error rather
  // than `errors.problem` (which only carries the generic text `present()` gives every 409).
  const problem = errors.problem?.kind === 'conflict' ? presentConflict(t, editing ? update.error : create.error) : errors.problem;

  return (
    <form aria-labelledby={headingId} onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3" noValidate>
      <h3 id={headingId} className="font-semibold">{heading}</h3>
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
        </>
      ) : null}
      {/* Mounted empty so a screen reader is already watching it when the text arrives. */}
      <p role="status" aria-live="polite">{saved ? t('envelope.flow_saved') : ''}</p>
      <Field label={t('envelope.date')} error={e.occurredOn?.message}>
        {(c) => <input type="date" max={todayIso()} {...c} {...form.register('occurredOn')} className={input} />}
      </Field>
      <Field label={t('envelope.kind')} error={e.kind?.message}>
        {(c) => (
          <select {...c} {...form.register('kind')} className={input}>
            {kinds.map((k) => <option key={k} value={k}>{t(`investments.flow_kind.${k}`)}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('envelope.amount')} error={e.amount?.message}>
        {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={`w-32 ${input}`} />}
      </Field>
      <Field label={t('envelope.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <div className="flex items-center gap-2">
        <Button type="submit" disabled={pending}>{editing ? t('envelope.save') : t('envelope.add_flow')}</Button>
        {editing ? <Button type="button" variant="ghost" onClick={() => onDone?.()}>{t('envelope.cancel')}</Button> : null}
      </div>
    </form>
  );
}
