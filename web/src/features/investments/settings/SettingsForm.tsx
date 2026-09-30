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
import { Field } from '../../subscriptions/edits/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { knownConflictMessage } from '../../subscriptions/fieldErrors';
import { ENVELOPE, invalidateInvestments, type EnvelopeDetail } from '../api';
import { placeFieldError, presentConflict } from '../entries/entries';
import { CADENCES, KINDS } from '../format';
import { toEditBody, toWireBody, type EditBody, type SettingsValues } from './settingsBody';

const input = 'rounded-md border px-3 py-2';
const PATHS = {
  displayName: 'displayName', kind: 'kind', institution: 'institution', accountHint: 'accountHint',
  valuationCadence: 'valuationCadence', staleAfterDays: 'staleAfterDays', note: 'note',
} as const;
/** Fields whose value the server can refuse with `investments.invalid_envelope` (it names none: only metadata). */
const TEXT_FIELDS = ['displayName', 'institution', 'accountHint', 'staleAfterDays', 'note'] as const;

const schema = (t: TFunction) => z.object({
  displayName: z.string().trim().min(1, t('edit.required')).max(100, t('investments_form.name_too_long')),
  kind: z.enum(KINDS),
  institution: z.string().max(100, t('investments_form.institution_too_long')),
  accountHint: z.string().trim().max(4, t('investments_form.account_hint_too_long')),
  valuationCadence: z.enum(CADENCES),
  staleAfterDays: z.string().refine((v) => {
    const days = v.trim();
    return days === '' || (/^\d+$/.test(days) && Number(days) >= 1 && Number(days) <= 3650);
  }, t('envelope.settings.stale_range')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});

/** The one field a refusal without a path can concern: the only text field this save changed, if exactly one. */
function soleChangedField(body: EditBody) {
  const changed = TEXT_FIELDS.filter((f) => body[f] !== undefined);
  return changed.length === 1 ? changed[0] : undefined;
}

/** The envelope as the form left it after a save: the trimmed values, an emptied optional field back to none. */
function applied(before: EnvelopeDetail, values: SettingsValues): EnvelopeDetail {
  const text = (v: string) => (v.trim() === '' ? null : v.trim());
  const days = values.staleAfterDays.trim();
  return {
    ...before, displayName: values.displayName.trim(), kind: values.kind, valuationCadence: values.valuationCadence,
    institution: text(values.institution), accountHint: text(values.accountHint), note: text(values.note),
    // A count of days, not an amount.
    staleAfterDays: days === '' ? null : Number(days),
  };
}

/**
 * 2B2b design: an open envelope's settings, sending only what changed. The page owns the status region (`onStart`
 * clears it when a save begins, `onDone` fills it), as for every other action on the detail page.
 */
export function SettingsForm({ envelope, onStart, onDone }: {
  envelope: EnvelopeDetail; onStart: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The server's kind and cadence are plain strings; the selects only offer the ones the screen knows.
  const defaults = {
    displayName: envelope.displayName, kind: envelope.kind as (typeof KINDS)[number], institution: envelope.institution ?? '',
    accountHint: envelope.accountHint ?? '', valuationCadence: envelope.valuationCadence as (typeof CADENCES)[number],
    staleAfterDays: envelope.staleAfterDays == null ? '' : String(envelope.staleAfterDays), note: envelope.note ?? '',
  };
  const form = useForm({ resolver: zodResolver(schema(t)), defaultValues: defaults });
  // What the form started from (and, after a save, what it saved): a save is diffed against this, never against the
  // live envelope, which a refetch may have changed under the person's hands. The form itself keeps the person's
  // values across such a refetch.
  const baseline = useRef(envelope);
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('patch', ENVELOPE, { onSuccess: () => invalidateInvestments(queryClient) });

  const submit = form.handleSubmit((values) => {
    onStart();
    errors.clear();
    const body = toEditBody(baseline.current, values);
    if (!body) { onDone(t('envelope.settings.nothing')); return; }
    save.mutate(
      { params: { path: { id: envelope.id } }, body: toWireBody(body) },
      {
        onSuccess: () => {
          baseline.current = applied(baseline.current, values);
          form.reset({ ...values, displayName: values.displayName.trim() });
          onDone(t('envelope.settings.saved'));
        },
        onError: (error) => {
          const owner = soleChangedField(body);
          placeFieldError(error, t, form.setError, errors, {
            'investments.unknown_kind': 'kind',
            'investments.unknown_cadence': 'valuationCadence',
            ...(owner ? { 'investments.invalid_envelope': owner } : {}),
          });
        },
      },
    );
  });

  const e = form.formState.errors;
  // Every refusal reads in French: a conflict by its own sentence, a 422 no field owns by its code's, never the
  // server's English title.
  const problem: Presentation | null =
    errors.problem?.kind === 'conflict' ? presentConflict(t, save.error)
      : errors.problem?.kind === 'validation'
        ? { ...errors.problem, detail: knownConflictMessage(t, toProblem(save.error).code) }
        : errors.problem;

  return (
    <form aria-label={t('envelope.settings.title')} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
        </>
      ) : null}
      <Field label={t('investments_form.name')} error={e.displayName?.message}>
        {(c) => <input {...c} {...form.register('displayName')} className={`w-full ${input}`} />}
      </Field>
      <Field label={t('investments_form.kind')} error={e.kind?.message}>
        {(c) => (
          <select {...c} {...form.register('kind')} className={input}>
            {KINDS.map((k) => <option key={k} value={k}>{t(`investments.kind.${k}`)}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('investments_form.institution')} error={e.institution?.message}>
        {(c) => <input {...c} {...form.register('institution')} className={`w-full ${input}`} />}
      </Field>
      <Field label={t('investments_form.account_hint')} hint={t('investments_form.account_hint_help')} error={e.accountHint?.message}>
        {(c) => <input {...c} {...form.register('accountHint')} className={`w-24 ${input}`} />}
      </Field>
      <Field label={t('investments_form.cadence')} error={e.valuationCadence?.message}>
        {(c) => (
          <select {...c} {...form.register('valuationCadence')} className={input}>
            {CADENCES.map((cadence) => <option key={cadence} value={cadence}>{t(`investments.cadence.${cadence}`)}</option>)}
          </select>
        )}
      </Field>
      <Field label={t('envelope.settings.stale_after')} hint={t('envelope.settings.stale_help')} error={e.staleAfterDays?.message}>
        {(c) => <input inputMode="numeric" {...c} {...form.register('staleAfterDays')} className={`w-24 ${input}`} />}
      </Field>
      <Field label={t('envelope.settings.note')} error={e.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className={`w-full ${input}`} />}
      </Field>
      <Button type="submit" disabled={save.isPending}>{t('envelope.settings.submit')}</Button>
    </form>
  );
}
