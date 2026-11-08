import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { ChevronRightIcon } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { Controller, useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { ownershipPath } from '@/components/ownership/ownership';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { CurrencyField } from '@/components/form/CurrencyField';
import { DateField } from '@/components/form/DateField';
import { SelectField } from '@/components/form/SelectField';
import { FormActions, FormPage } from '@/components/page/FormPage';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Input } from '@/components/ui/input';
import { Field } from '@/components/form/Field';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { PERSONS } from '@/api/persons';
import { todayIso } from '@/lib/today';
import { ENVELOPES, invalidateInvestments } from './api';
import { CADENCES, KINDS, regulatedOptions } from './format';
import { newEnvelopeSchema, NEW_ENVELOPE_PATHS, toCreateBody, type NewEnvelopeValues } from './newEnvelopeSchema';
import '@/locales/investments';

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
      displayName: '', kind: 'savings', currency: 'EUR', institution: '', accountHint: '', valuationCadence: 'monthly', regulated: 'none',
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
  const [moreOpen, setMoreOpen] = useState(false);

  return (
    <FormPage crumbs={[{ label: t('nav.investments'), to: '/investments' }]} title={t('investments_form.title')}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <form noValidate onSubmit={(e) => void form.handleSubmit((values) => create.mutate({ body: toCreateBody(values) }))(e)} className="space-y-4">
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
          {form.watch('kind') === 'savings' ? (
            <Controller control={form.control} name="regulated" render={({ field }) => (
              <Field label={t('envelope.settings.regulated')} hint={t('envelope.settings.regulated_help')} error={e.regulated?.message}>
                {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={regulatedOptions(t)} />}
              </Field>
            )} />
          ) : null}
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
        </div>
        <OwnershipFields form={form} persons={persons} />
        <Fold label={t('investments_form.with_opening')} hint={t('investments_form.with_opening_help')} open={withOpening}
          onOpenChange={(open) => form.setValue('withOpening', open)}>
          <Controller control={form.control} name="openingOn" render={({ field }) => (
            <Field label={t('investments_form.opening_on')} error={e.openingOn?.message}>
              {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
            </Field>
          )} />
          <Field label={t('investments_form.opening_amount')} error={e.openingAmount?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register('openingAmount')} />}
          </Field>
        </Fold>
        <Fold label={t('investments_form.with_first_value')} open={withFirstValue}
          onOpenChange={(open) => form.setValue('withFirstValue', open)}>
          <Controller control={form.control} name="firstValueOn" render={({ field }) => (
            <Field label={t('investments_form.first_value_on')} error={e.firstValueOn?.message}>
              {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
            </Field>
          )} />
          <Field label={t('investments_form.first_value_amount')} error={e.firstValueAmount?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register('firstValueAmount')} />}
          </Field>
        </Fold>
        <Fold label={t('investments_form.more')} open={moreOpen || !!e.currency} onOpenChange={setMoreOpen}>
          <Controller control={form.control} name="currency" render={({ field }) => (
            <Field label={t('investments_form.currency')} error={e.currency?.message}>
              {(c) => <CurrencyField {...c} value={field.value} onValueChange={field.onChange} />}
            </Field>
          )} />
        </Fold>
        <FormActions><Button type="submit" disabled={create.isPending}>{t('investments_form.submit')}</Button></FormActions>
      </form>
    </FormPage>
  );
}

/** A folded group of fields: the trigger names it, its fields (two per row on desktop) show once it is open. */
function Fold({ label, hint, open, onOpenChange, children }: {
  label: string; hint?: string; open: boolean; onOpenChange: (open: boolean) => void; children: ReactNode;
}) {
  return (
    <Collapsible open={open} onOpenChange={onOpenChange} className="space-y-2">
      <CollapsibleTrigger asChild>
        <Button type="button" variant="ghost" size="sm" className="group -ml-3">
          <ChevronRightIcon aria-hidden="true" className="transition-transform group-data-[state=open]:rotate-90" />
          {label}
        </Button>
      </CollapsibleTrigger>
      {hint ? <p className="text-sm text-muted-foreground">{hint}</p> : null}
      <CollapsibleContent className="grid gap-3 sm:grid-cols-2">{children}</CollapsibleContent>
    </Collapsible>
  );
}
