import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useRef } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Presentation } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Field } from '@/components/form/Field';
import { AMOUNT, ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { knownConflictMessage } from '../../subscriptions/fieldErrors';
import { todayIso } from '../../subscriptions/today';
import { ENVELOPE, invalidateInvestments, type EnvelopeDetail } from '../api';
import { placeFieldError, presentConflict } from '../entries/entries';
import { CADENCES, KINDS } from '../format';
import { toEditBody, toWireBody, type EditBody, type SettingsValues } from './settingsBody';
import '@/locales/investments';

const PATHS = {
  displayName: 'displayName', kind: 'kind', institution: 'institution', accountHint: 'accountHint',
  valuationCadence: 'valuationCadence', openedOn: 'openedOn', premiumsBefore2017: 'premiumsBefore2017', staleAfterDays: 'staleAfterDays', ucShareFloorPct: 'ucShareFloorPct', note: 'note',
} as const;
/** Fields whose value the server can refuse with `investments.invalid_envelope` (the server names the field; `soleChangedField` is the fallback for a refusal without one). */
const TEXT_FIELDS = ['displayName', 'institution', 'accountHint', 'staleAfterDays', 'ucShareFloorPct', 'note', 'openedOn', 'premiumsBefore2017'] as const;

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
  ucShareFloorPct: z.string().refine((v) => {
    const floor = v.trim().replace(',', '.');
    return floor === '' || (/^\d{1,3}(\.\d{1,4})?$/.test(floor) && Number(floor) <= 100);
  }, t('envelope.settings.uc_floor_range')),
  openedOn: z.string().refine((v) => v === '' || ISO_DATE.test(v), t('envelope.settings.opened_on_format')),
  premiumsBefore2017: z.string(),
  note: z.string().max(2000, t('investments_form.note_too_long')),
}).superRefine((v, ctx) => {
  // A hidden field (another kind picked) never blocks a save.
  const premiums = v.premiumsBefore2017.replace(/\s/g, '');
  if (v.kind === 'life_insurance' && premiums !== '' && !AMOUNT.test(premiums))
    ctx.addIssue({ code: 'custom', path: ['premiumsBefore2017'], message: t('envelope.settings.premiums_format') });
});

/** The one field a refusal without a path can concern: the only text field this save changed, if exactly one. */
function soleChangedField(body: EditBody) {
  const changed = TEXT_FIELDS.filter((f) => body[f] !== undefined);
  return changed.length === 1 ? changed[0] : undefined;
}

/** The premiums the server keeps after a save: only a life insurance that stayed one (a kind change drops them, and never sends them). */
const keepsPremiums = (before: EnvelopeDetail, values: SettingsValues) => before.kind === 'life_insurance' && values.kind === 'life_insurance';

/** The envelope as the form left it after a save: the trimmed values, an emptied optional field back to none. */
function applied(before: EnvelopeDetail, values: SettingsValues): EnvelopeDetail {
  const text = (v: string) => (v.trim() === '' ? null : v.trim());
  const days = values.staleAfterDays.trim();
  const floor = values.ucShareFloorPct.trim().replace(',', '.');
  const premiums = toWireAmount(values.premiumsBefore2017.replace(/\s/g, ''));
  return {
    ...before, displayName: values.displayName.trim(), kind: values.kind, valuationCadence: values.valuationCadence,
    institution: text(values.institution), accountHint: text(values.accountHint), note: text(values.note),
    openedOn: text(values.openedOn),
    premiumsBefore2017: !keepsPremiums(before, values) || premiums === '' ? null
      : { amount: premiums, currency: before.currency },
    // A count of days, not an amount.
    staleAfterDays: days === '' ? null : Number(days),
    ucShareFloorPct: floor === '' ? null : Number(floor),
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
    ucShareFloorPct: envelope.ucShareFloorPct == null ? '' : String(envelope.ucShareFloorPct),
    openedOn: envelope.openedOn ?? '', premiumsBefore2017: envelope.premiumsBefore2017?.amount ?? '',
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
          const keeps = keepsPremiums(baseline.current, values);
          baseline.current = applied(baseline.current, values);
          form.reset({ ...values, displayName: values.displayName.trim(), premiumsBefore2017: keeps ? values.premiumsBefore2017 : '' });
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
    <form aria-label={t('envelope.settings.title')} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-2xl space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateInvestments(queryClient)} /> : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('investments_form.name')} error={e.displayName?.message}>
          {(c) => <Input {...c} {...form.register('displayName')} />}
        </Field>
        <Controller control={form.control} name="kind" render={({ field }) => (
          <Field label={t('investments_form.kind')} error={e.kind?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={KINDS.map((k) => ({ value: k, label: t(`investments.kind.${k}`) }))} />}
          </Field>
        )} />
        <Field label={t('investments_form.institution')} error={e.institution?.message}>
          {(c) => <Input {...c} {...form.register('institution')} />}
        </Field>
        <Field label={t('investments_form.account_hint')} hint={t('investments_form.account_hint_help')} error={e.accountHint?.message}>
          {(c) => <Input {...c} {...form.register('accountHint')} />}
        </Field>
        <Controller control={form.control} name="valuationCadence" render={({ field }) => (
          <Field label={t('investments_form.cadence')} error={e.valuationCadence?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={CADENCES.map((cadence) => ({ value: cadence, label: t(`investments.cadence.${cadence}`) }))} />}
          </Field>
        )} />
        <Controller control={form.control} name="openedOn" render={({ field }) => (
          <Field label={t('envelope.settings.opened_on')} hint={t('envelope.settings.opened_on_help')} error={e.openedOn?.message}>
            {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
        {/* The kind as the person is editing it: the field disappears as soon as another kind is picked. */}
        {envelope.kind === 'life_insurance' && form.watch('kind') === 'life_insurance' ? (
          <Field label={t('envelope.settings.premiums_before_2017')} hint={t('envelope.settings.premiums_before_2017_help')} error={e.premiumsBefore2017?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register('premiumsBefore2017')} />}
          </Field>
        ) : null}
        <Field label={t('envelope.settings.stale_after')} hint={t('envelope.settings.stale_help')} error={e.staleAfterDays?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register('staleAfterDays')} />}
        </Field>
        <Field label={t('envelope.settings.uc_floor')} hint={t('envelope.settings.uc_floor_help')} error={e.ucShareFloorPct?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('ucShareFloorPct')} />}
        </Field>
      </div>
      <Field label={t('envelope.settings.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <Button type="submit" disabled={save.isPending}>{t('envelope.settings.submit')}</Button>
    </form>
  );
}
