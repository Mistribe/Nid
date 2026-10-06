import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useRef } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentConflict } from '../../investments/entries/entries';
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { knownConflictMessage } from '../../subscriptions/fieldErrors';
import { invalidateIncome, SOURCE, type IncomeSourceDetail } from '../api';
import { KINDS } from '../format';
import { toEditBody, toWireBody, type SettingsValues } from './settingsBody';

const input = 'rounded-md border px-3 py-2';
const PATHS = { displayName: 'displayName', kind: 'kind', payerLabel: 'payerLabel', note: 'note' } as const;

const schema = (t: TFunction) => z.object({
  displayName: z.string().trim().min(1, t('edit.required')).max(100, t('investments_form.name_too_long')),
  kind: z.enum(KINDS),
  payerLabel: z.string().max(100, t('income_form.payer_too_long')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});

/** The source as the form left it after a save: trimmed, an emptied optional field back to none. */
function applied(before: IncomeSourceDetail, values: SettingsValues): IncomeSourceDetail {
  const text = (v: string) => (v.trim() === '' ? null : v.trim());
  return { ...before, displayName: values.displayName.trim(), kind: values.kind, payerLabel: text(values.payerLabel), note: text(values.note) };
}

/**
 * 2C2a design §7: name, kind, payer label and note, sending only what changed (currency, owners and visibility are not
 * edited). The page owns the status region: `onStart` clears it, `onDone` fills it.
 */
export function SettingsForm({ source, onStart, onDone }: { source: IncomeSourceDetail; onStart: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The server's kind is a plain string; the select only offers the ones the screen knows.
  const form = useForm({
    resolver: zodResolver(schema(t)),
    defaultValues: { displayName: source.displayName, kind: source.kind as (typeof KINDS)[number], payerLabel: source.payerLabel ?? '', note: source.note ?? '' },
  });
  // Diffed against what the form started from (or last saved), never the live source a refetch may have changed.
  const baseline = useRef(source);
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('patch', SOURCE, { onSuccess: () => invalidateIncome(queryClient) });

  const submit = form.handleSubmit((values) => {
    onStart();
    errors.clear();
    const body = toEditBody(baseline.current, values);
    if (!body) { onDone(t('income_detail.settings.nothing')); return; }
    save.mutate(
      { params: { path: { id: source.id } }, body: toWireBody(body) },
      {
        onSuccess: () => {
          baseline.current = applied(baseline.current, values);
          form.reset({ ...values, displayName: values.displayName.trim() });
          onDone(t('income_detail.settings.saved'));
        },
        onError: (error) => errors.onError(error),
      },
    );
  });

  const e = form.formState.errors;
  // Every refusal reads in French: a conflict by its own sentence, a 422 no field owns by its code's.
  const problem: Presentation | null =
    errors.problem?.kind === 'conflict' ? presentConflict(t, save.error)
      : errors.problem?.kind === 'validation' ? { ...errors.problem, detail: knownConflictMessage(t, toProblem(save.error).code) }
        : errors.problem;

  return (
    <form aria-label={t('income_detail.settings.title')} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateIncome(queryClient)} /> : null}
        </>
      ) : null}
      <Field label={t('income_form.name')} error={e.displayName?.message}>
        {(c) => <input {...c} {...form.register('displayName')} className={`w-full ${input}`} />}
      </Field>
      <Field label={t('income_form.kind')} error={e.kind?.message}>
        {(c) => (
          <select {...c} {...form.register('kind')} className={input}>
            {KINDS.map((k) => <option key={k} value={k}>{t(`income.kind.${k}`)}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('income_form.payer')} hint={t('income_form.payer_help')} error={e.payerLabel?.message}>
        {(c) => <input {...c} {...form.register('payerLabel')} className={`w-full ${input}`} />}
      </Field>
      <Field label={t('income_form.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <Button type="submit" disabled={save.isPending}>{t('income_detail.settings.submit')}</Button>
    </form>
  );
}
