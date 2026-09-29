import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { fieldErrorsOf, messageFor } from '../../subscriptions/fieldErrors';
import { todayIso } from '../../subscriptions/today';
import { FLOW, FLOWS, invalidateInvestments, type EnvelopeDetail, type Flow } from '../api';
import { FLOW_KINDS } from '../format';
import { flowSchema, trimAmount } from './entries';

const input = 'rounded-md border px-3 py-2';
const PATHS = { occurredOn: 'occurredOn', kind: 'kind', amount: 'amount', note: 'note' } as const;

/** A pathless Core refusal (Task 3: these codes carry no `errors[]`) attributed to the one field it can concern. */
function fallbackField(code: string | null | undefined): 'occurredOn' | 'amount' | undefined {
  if (code === 'core.date_in_future') return 'occurredOn';
  if (code === 'core.currency_mismatch' || code === 'core.invalid_fact') return 'amount';
  return undefined;
}

/**
 * The « Ajouter un flux » form at the top of the envelope's page (`editing` absent), or a correction inline on its
 * row (2B2a design §6): a new flow posts `FLOWS`, a correction puts `FLOW`. « Versements antérieurs » (`opening`)
 * is offered only while the envelope has none yet, unless this very form is correcting that one.
 */
export function FlowForm({ envelope, editing, initialKind, onDone }: {
  envelope: EnvelopeDetail; editing?: Flow; initialKind?: 'opening'; onDone?: () => void;
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
      kind: (editing?.kind as (typeof FLOW_KINDS)[number] | undefined) ?? initialKind ?? 'contribution',
      amount: editing ? trimAmount(editing.amount.amount) : '',
      note: editing?.note ?? '',
    },
  });
  useEffect(() => {
    if (initialKind && !editing) { form.setValue('kind', initialKind); form.setFocus('kind'); }
    // Reacts only to the page setting `initialKind` from the missing-opening warning; `form` and `editing` are stable here.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initialKind]);

  const errors = useSubmitErrors(form.setError, PATHS);
  const onFail = (error: unknown) => {
    setSaved(false);
    const known = fieldErrorsOf(error);
    const field = known.length === 0 ? fallbackField(toProblem(error).code) : undefined;
    if (field) {
      const problem = toProblem(error);
      form.setError(field, { type: 'server', message: messageFor(t, { path: field, code: problem.code ?? '', message: problem.title ?? '' }) });
      errors.clear();
    } else {
      errors.onError(error);
    }
  };
  const create = $api.useMutation('post', FLOWS, {
    onSuccess: () => {
      setSaved(true);
      errors.clear();
      void invalidateInvestments(queryClient);
      form.reset({ occurredOn: todayIso(), kind: initialKind ?? 'contribution', amount: '', note: '' });
    },
    onError: onFail,
  });
  // A correction stays open on its « Corrigé » status; the person dismisses it with « Annuler » (onDone), rather
  // than the row silently snapping shut the instant the status would have appeared.
  const update = $api.useMutation('put', FLOW, {
    onSuccess: () => { setSaved(true); errors.clear(); void invalidateInvestments(queryClient); },
    onError: onFail,
  });

  const submit = form.handleSubmit((values) => {
    setSaved(false);
    const body = {
      occurredOn: values.occurredOn, kind: values.kind,
      amount: { amount: toWireAmount(values.amount), currency: envelope.currency },
      note: values.note || undefined,
    };
    if (editing) update.mutate({ params: { path: { id: envelope.id, flowId: editing.id } }, body });
    else create.mutate({ params: { path: { id: envelope.id } }, body });
  });

  const e = form.formState.errors;
  const pending = editing ? update.isPending : create.isPending;
  const heading = editing ? t('envelope.edit_flow') : t('envelope.new_flow');

  return (
    <form aria-labelledby={headingId} onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3" noValidate>
      <h3 id={headingId} className="font-semibold">{heading}</h3>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      {saved ? <p role="status">{editing ? t('envelope.flow_corrected') : t('envelope.flow_saved')}</p> : null}
      <Field label={t('envelope.date')} error={e.occurredOn?.message}>
        {(c) => <input type="date" {...c} {...form.register('occurredOn')} className={input} />}
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
