import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useRef } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { presentConflict } from '../../investments/entries/entries';
import { Field } from '@/components/form/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { knownConflictMessage } from '../../subscriptions/fieldErrors';
import { invalidateProperties } from '../../properties/api';
import { invalidateLoans, LOAN, type LoanDetail } from '../api';
import { PropertySelect } from '../PropertySelect';
import { toEditBody, toWireBody, type SettingsValues } from './settingsBody';

const PATHS = { name: 'name', lender: 'lender', note: 'note', propertyId: 'propertyId' } as const;

const schema = (t: TFunction) => z.object({
  name: z.string().trim().min(1, t('edit.required')).max(100, t('investments_form.name_too_long')),
  lender: z.string().max(100, t('loan_form.lender_too_long')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
  propertyId: z.string(),
});

/** The loan as the form left it after a save: trimmed, an emptied optional field back to none. */
function applied(before: LoanDetail, values: SettingsValues): LoanDetail {
  const text = (v: string) => (v.trim() === '' ? null : v.trim());
  return { ...before, name: values.name.trim(), lender: text(values.lender), note: text(values.note), propertyId: values.propertyId === '' ? null : values.propertyId };
}

/**
 * 3B1 design §7: name, lender and note, sending only what changed (borrowers, visibility and currency are not edited).
 * The page owns the status region: `onStart` clears it, `onDone` fills it.
 */
export function SettingsForm({ loan, onStart, onDone }: { loan: LoanDetail; onStart: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<SettingsValues>({
    resolver: zodResolver(schema(t)),
    defaultValues: { name: loan.name, lender: loan.lender ?? '', note: loan.note ?? '', propertyId: loan.propertyId ?? '' },
  });
  // Diffed against what the form started from (or last saved), never the live loan a refetch may have changed.
  const baseline = useRef(loan);
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('patch', LOAN, {
    // The properties are refreshed in the background: a slow list must not hold the save's confirmation.
    onSuccess: () => { void invalidateProperties(queryClient); return invalidateLoans(queryClient); },
  });

  const submit = form.handleSubmit((values) => {
    onStart();
    errors.clear();
    const body = toEditBody(baseline.current, values);
    if (!body) { onDone(t('loan_detail.settings.nothing')); return; }
    save.mutate(
      { params: { path: { id: loan.id } }, body: toWireBody(body) },
      {
        onSuccess: () => {
          baseline.current = applied(baseline.current, values);
          form.reset({ ...values, name: values.name.trim() });
          onDone(t('loan_detail.settings.saved'));
        },
        onError: (error) => errors.onError(error),
      },
    );
  }, onStart); // an invalid submit clears the previous status too

  const e = form.formState.errors;
  // Every refusal reads in French: a conflict by its own sentence, a 422 no field owns by its code's.
  const problem: Presentation | null =
    errors.problem?.kind === 'conflict' ? presentConflict(t, save.error)
      : errors.problem?.kind === 'validation' ? { ...errors.problem, detail: knownConflictMessage(t, toProblem(save.error).code) }
        : errors.problem;

  return (
    <form aria-label={t('loan_detail.settings.title')} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' ? <Reload onReload={() => invalidateLoans(queryClient)} /> : null}
        </>
      ) : null}
      <Field label={t('loan_form.name')} error={e.name?.message}>
        {(c) => <Input {...c} {...form.register('name')} />}
      </Field>
      <Field label={t('loan_form.lender')} hint={t('loan_form.lender_help')} error={e.lender?.message}>
        {(c) => <Input {...c} {...form.register('lender')} />}
      </Field>
      {/* What the form opened on (or last saved) is linked: its option stays whatever the list says (precision 10). */}
      <Controller control={form.control} name="propertyId" render={({ field }) => (
        <PropertySelect value={field.value} onValueChange={field.onChange} error={e.propertyId?.message}
          linked={baseline.current.propertyId ? { id: baseline.current.propertyId, name: baseline.current.propertyName ?? null } : undefined} />
      )} />
      <Field label={t('loan_form.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <Button type="submit" disabled={save.isPending}>{t('loan_detail.settings.submit')}</Button>
    </form>
  );
}
