import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { ownershipPath } from '@/components/ownership/ownership';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Field } from '../subscriptions/edits/Field';
import { useSubmitErrors } from '../subscriptions/edits/useSubmitErrors';
import { fieldErrorsOf, messageFor } from '../subscriptions/fieldErrors';
import { PERSONS } from '../subscriptions/api';
import { todayIso } from '../subscriptions/today';
import { ENVELOPES, invalidateInvestments } from './api';
import { CADENCES, KINDS } from './format';
import { newEnvelopeSchema, NEW_ENVELOPE_PATHS, toCreateBody, type NewEnvelopeValues } from './newEnvelopeSchema';

const input = 'rounded-md border px-3 py-2';

/** A pathless refusal (the Core fact codes carry no path, 2A) attributed to the one date/amount field it can
 * concern: the first-value field before the opening one, and only when exactly one such field was filled. */
const CODE_CANDIDATES: Record<string, readonly ('firstValueOn' | 'openingOn' | 'firstValueAmount' | 'openingAmount')[]> = {
  'core.date_in_future': ['firstValueOn', 'openingOn'],
  'core.currency_mismatch': ['firstValueAmount', 'openingAmount'],
  'core.amount_not_positive': ['firstValueAmount', 'openingAmount'],
};

function fallbackField(code: string | null | undefined, values: NewEnvelopeValues) {
  const candidates = code ? CODE_CANDIDATES[code] : undefined;
  if (!candidates) return undefined;
  const filled = candidates.filter((f) => (f === 'firstValueOn' || f === 'firstValueAmount' ? values.withFirstValue : values.withOpening));
  return filled.length === 1 ? filled[0] : undefined;
}

/** 2B2a design: the entry wizard for a new envelope, its opening and its first value in one request. */
export function NewEnvelope() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const persons = people.data ?? [];
  const form = useForm<NewEnvelopeValues>({
    resolver: zodResolver(newEnvelopeSchema(t)),
    defaultValues: {
      displayName: '', kind: 'savings', currency: 'EUR', institution: '', accountHint: '', valuationCadence: 'monthly',
      visibility: 'shared', owners: [{ personId: session.person.id, share: '100' }],
      withOpening: false, openingOn: '', openingAmount: '',
      withFirstValue: false, firstValueOn: todayIso(), firstValueAmount: '',
    },
  });
  const pathOf = (path: string): Path<NewEnvelopeValues> | undefined =>
    (NEW_ENVELOPE_PATHS[path] as Path<NewEnvelopeValues> | undefined) ?? ownershipPath(path);
  const errors = useSubmitErrors(form.setError, pathOf);
  const create = $api.useMutation('post', ENVELOPES, {
    onSuccess: ({ id }) => {
      void invalidateInvestments(queryClient);
      void navigate({ to: '/investments/$id', params: { id } });
    },
    onError: (error) => {
      const problem = toProblem(error);
      const known = fieldErrorsOf(error);
      const field = known.length === 0 ? fallbackField(problem.code, form.getValues()) : undefined;
      if (field) {
        form.setError(field, { type: 'server', message: messageFor(t, { path: field, code: problem.code ?? '', message: problem.title ?? '' }) });
        errors.clear();
      } else {
        errors.onError(error);
      }
    },
  });

  const e = form.formState.errors;
  const withOpening = form.watch('withOpening');
  const withFirstValue = form.watch('withFirstValue');

  return (
    <article className="max-w-2xl space-y-4">
      <h1 className="text-2xl font-semibold">{t('investments_form.title')}</h1>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <form noValidate onSubmit={(e) => void form.handleSubmit((values) => create.mutate({ body: toCreateBody(values) }))(e)} className="space-y-4">
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
        <Field label={t('investments_form.currency')} error={e.currency?.message}>
          {(c) => <input maxLength={3} {...c} {...form.register('currency')} className={`w-20 uppercase ${input}`} />}
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
        <OwnershipFields form={form} persons={persons} />
        <Field label={t('investments_form.with_opening')} hint={t('investments_form.with_opening_help')} inline>
          {(c) => <input type="checkbox" {...c} {...form.register('withOpening')} />}
        </Field>
        {withOpening ? (
          <>
            <Field label={t('investments_form.opening_on')} error={e.openingOn?.message}>
              {(c) => <input type="date" max={todayIso()} {...c} {...form.register('openingOn')} className={input} />}
            </Field>
            <Field label={t('investments_form.opening_amount')} error={e.openingAmount?.message}>
              {(c) => <input inputMode="decimal" {...c} {...form.register('openingAmount')} className={`w-32 ${input}`} />}
            </Field>
          </>
        ) : null}
        <Field label={t('investments_form.with_first_value')} inline>
          {(c) => <input type="checkbox" {...c} {...form.register('withFirstValue')} />}
        </Field>
        {withFirstValue ? (
          <>
            <Field label={t('investments_form.first_value_on')} error={e.firstValueOn?.message}>
              {(c) => <input type="date" max={todayIso()} {...c} {...form.register('firstValueOn')} className={input} />}
            </Field>
            <Field label={t('investments_form.first_value_amount')} error={e.firstValueAmount?.message}>
              {(c) => <input inputMode="decimal" {...c} {...form.register('firstValueAmount')} className={`w-32 ${input}`} />}
            </Field>
          </>
        ) : null}
        <Button type="submit" disabled={create.isPending}>{t('investments_form.submit')}</Button>
      </form>
    </article>
  );
}
