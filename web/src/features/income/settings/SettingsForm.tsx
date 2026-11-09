import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useRef } from 'react';
import { Controller, useForm, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Presentation } from '@/api/errors';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { presentConflict, knownConflictMessage } from '@/components/form/fieldErrors';
import { Field } from '@/components/form/Field';
import { Reload } from '@/components/form/Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { invalidateIncome, SOURCE, type IncomeSourceDetail } from '@/api/income';
import { PERSONS } from '@/api/persons';
import { checkEditOwnership, editOwnershipShape, ownershipApplied, ownershipDefaults, ownershipPath, type OwnershipValues } from '@/components/ownership/ownership';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { ITEM_SHARING } from '@/components/ownership/SharingBadges';
import { KINDS } from '../format';
import { toEditBody, toWireBody, type SettingsValues } from './settingsBody';
import '@/locales/income';
import '@/locales/investments';
import '@/locales/forms';

const PATHS = { displayName: 'displayName', kind: 'kind', payerLabel: 'payerLabel', note: 'note' } as const;

const schema = (t: TFunction) => z.object({
  displayName: z.string().trim().min(1, t('edit.required')).max(100, t('investments_form.name_too_long')),
  kind: z.enum(KINDS),
  payerLabel: z.string().max(100, t('income_form.payer_too_long')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
  ...editOwnershipShape(t),
}).superRefine((v, ctx) => checkEditOwnership(v, ctx, t));

/** The source as the form left it after a save: trimmed, an emptied optional field back to none. */
function applied(before: IncomeSourceDetail, values: SettingsValues): IncomeSourceDetail {
  const text = (v: string) => (v.trim() === '' ? null : v.trim());
  return {
    ...before, displayName: values.displayName.trim(), kind: values.kind, payerLabel: text(values.payerLabel), note: text(values.note),
    ...(values.visibility && values.owners ? ownershipApplied({ visibility: values.visibility, shareLevel: values.shareLevel, owners: values.owners }) : {}),
  };
}

/**
 * 2C2a design §7: name, kind, payer label and note, sending only what changed (the currency is not edited); #69: the
 * visibility, sharing level and owners for the source's owners. `onDone` gets the confirmation, which the page shows as a toast.
 */
export function SettingsForm({ source, onDone }: { source: IncomeSourceDetail; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The server's kind is a plain string; the select only offers the ones the screen knows.
  const form = useForm({
    resolver: zodResolver(schema(t)),
    defaultValues: {
      displayName: source.displayName, kind: source.kind as (typeof KINDS)[number], payerLabel: source.payerLabel ?? '', note: source.note ?? '',
      // #69: shown, and so sent, to its owners only.
      ...(source.canChangeOwners ? ownershipDefaults(source) : {}),
    },
  });
  // Diffed against what the form started from (or last saved), never the live source a refetch may have changed.
  const baseline = useRef(source);
  const errors = useSubmitErrors(form.setError, (path) => PATHS[path as keyof typeof PATHS] ?? ownershipPath(path));
  const persons = $api.useQuery('get', PERSONS, {}, { enabled: source.canChangeOwners }).data ?? [];
  const save = $api.useMutation('patch', SOURCE, {
    onSuccess: () => {
      void invalidateIncome(queryClient);
      void queryClient.invalidateQueries({ queryKey: ['get', ITEM_SHARING] });
    },
  });

  const submit = form.handleSubmit((values) => {
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
    <form aria-label={t('income_detail.settings.title')} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-xl space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateIncome(queryClient)} /> : null}
        </>
      ) : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('income_form.name')} error={e.displayName?.message}>
          {(c) => <Input {...c} {...form.register('displayName')} />}
        </Field>
        <Controller control={form.control} name="kind" render={({ field }) => (
          <Field label={t('income_form.kind')} error={e.kind?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={KINDS.map((k) => ({ value: k, label: t(`income.kind.${k}`) }))} />}
          </Field>
        )} />
      </div>
      <Field label={t('income_form.payer')} hint={t('income_form.payer_help')} error={e.payerLabel?.message}>
        {(c) => <Input {...c} {...form.register('payerLabel')} />}
      </Field>
      <Field label={t('income_form.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      {/* Shown only when the defaults hold the ownership: its values are never undefined here. */}
      {source.canChangeOwners ? <OwnershipFields form={form as unknown as UseFormReturn<OwnershipValues>} persons={persons} /> : null}
      <Button type="submit" disabled={save.isPending}>{t('income_detail.settings.submit')}</Button>
    </form>
  );
}
