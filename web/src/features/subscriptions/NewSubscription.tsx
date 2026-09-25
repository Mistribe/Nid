import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import { useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { invalidateSubscriptions, LIST, PERSONS } from './api';
import { useSubmitErrors } from './edits/useSubmitErrors';
import { fieldErrorsOf, stepOf } from './fieldErrors';
import { fieldOf, STEP_PATHS, toCreateBody, wizardSchema, type WizardValues } from './wizard/schema';
import { CommonStep, OfferStep, SummaryStep } from './wizard/steps';

const STEP_FIELDS: Path<WizardValues>[][] = [
  ['provider', 'displayName', 'visibility', 'owners', 'users', 'note'],
  ['startsOn', 'amount', 'currency', 'everyCount', 'everyUnit', 'sameCovers', 'coversCount', 'coversUnit', 'maxCycles', 'payerType', 'payerPerson'],
];

/** 1E2a design §5: the recurring offer in three steps, sent as one request at the end. */
export function NewSubscription() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [step, setStep] = useState(0);
  const people = $api.useQuery('get', PERSONS);
  const persons = people.data ?? [];
  const schema = useMemo(() => wizardSchema(t), [t]);
  const form = useForm<WizardValues>({
    resolver: zodResolver(schema),
    mode: 'onTouched',
    defaultValues: {
      provider: null, displayName: '', visibility: 'shared', owners: [{ personId: session.person.id, share: '100' }], users: [], note: '',
      startsOn: '', amount: '', currency: 'EUR', everyCount: '1', everyUnit: 'month', sameCovers: true, coversCount: '1', coversUnit: 'month',
      maxCycles: '', payerType: 'household', payerPerson: '',
    },
  });
  const errors = useSubmitErrors(form.setError, (path) => fieldOf(path));
  const [pendingErrors, setPendingErrors] = useState<unknown>(null);
  const create = $api.useMutation('post', LIST, {
    onSuccess: (created) => {
      void invalidateSubscriptions(queryClient);
      void navigate({ to: '/subscriptions/$id', params: { id: created.id } });
    },
    onError: (error) => {
      const steps = fieldErrorsOf(error).map((f) => stepOf(f.path, STEP_PATHS)).filter((s): s is number => s !== undefined);
      if (steps.length > 0) setStep(Math.min(...steps));
      setPendingErrors(error);
    },
  });

  // Errors are placed once the step that owns them is on screen, so each lands on a mounted field.
  useEffect(() => {
    if (pendingErrors === null) return;
    errors.onError(pendingErrors);
    setPendingErrors(null);
  }, [pendingErrors, step, errors]);

  const next = async () => {
    if (await form.trigger(STEP_FIELDS[step])) setStep(step + 1);
  };
  const titles = [t('wizard.step_common'), t('wizard.step_offer'), t('wizard.step_summary')];

  return (
    <div className="max-w-2xl space-y-4">
      <Link to="/subscriptions" className="text-sm underline">{t('subscription.back')}</Link>
      <h1 className="text-2xl font-semibold">{t('wizard.title')}</h1>
      <p aria-live="polite" className="text-sm text-muted-foreground">{t('wizard.step_progress', { n: step + 1 })} · {titles[step]}</p>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      {people.error ? <ProblemMessage problem={present(toProblem(people.error))} /> : null}
      {/* The owner and payer selects are born with their options: a select registered before them would show its
          first option while the form holds the reader (final review). */}
      {!people.data ? <div aria-busy="true" aria-label={t('wizard.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /> : (
      <form noValidate onSubmit={(e) => {
        e.preventDefault();
        if (step < 2) void next();
        else void form.handleSubmit((values) => create.mutate({ body: toCreateBody(values) }))(e);
      }} className="space-y-4">
        {step === 0 ? <CommonStep form={form} persons={persons} /> : null}
        {step === 1 ? <OfferStep form={form} persons={persons} /> : null}
        {step === 2 ? <SummaryStep form={form} persons={persons} /> : null}
        <div className="flex gap-2">
          {step > 0 ? <Button type="button" variant="outline" onClick={() => setStep(step - 1)}>{t('wizard.back')}</Button> : null}
          {step < 2
            ? <Button type="submit">{t('wizard.next')}</Button>
            : <Button type="submit" disabled={create.isPending}>{t('wizard.submit')}</Button>}
        </div>
      </form>
      )}
    </div>
  );
}
