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
import { FormPage } from '@/components/page/FormPage';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Input } from '@/components/ui/input';
import { Field } from '@/components/form/Field';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { PERSONS } from '@/api/persons';
import { todayIso } from '@/lib/today';
import { ENVELOPES, invalidateInvestments } from '@/api/investments';
import { CADENCES, KINDS, regulatedOptions } from './format';
import { hasFirstValue, newEnvelopeSchema, NEW_ENVELOPE_PATHS, toCreateBody, type NewEnvelopeValues } from './newEnvelopeSchema';
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
  const filled = candidates.filter((f) => (f === 'firstValueOn' || f === 'firstValueAmount' ? hasFirstValue(values) : values.withOpening));
  return filled.length === 1 ? filled[0] : undefined;
}

/** Each step's fields, for « Continuer » and to go back to the step a server refusal names. */
const STEPS = {
  kind: ['kind', 'regulated', 'displayName'],
  where: ['institution', 'accountHint', 'visibility', 'owners'],
  value: ['firstValueAmount', 'firstValueOn', 'withOpening', 'openingOn', 'openingAmount', 'valuationCadence', 'currency'],
} as const;
const stepOf = (field: string) => (Object.keys(STEPS) as (keyof typeof STEPS)[]).find((id) => (STEPS[id] as readonly string[]).includes(field.split('.')[0]!));

/**
 * 2B2a design, in plain questions (design-system §6.2): what kind of account, where it is held, what is on it today.
 * The envelope, its opening and its first value go in one request; the cadence and the currency keep their defaults, folded.
 */
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
      firstValueOn: todayIso(), firstValueAmount: '',
    },
  });
  const pathOf = (path: string): Path<NewEnvelopeValues> | undefined =>
    (NEW_ENVELOPE_PATHS[path] as Path<NewEnvelopeValues> | undefined) ?? ownershipPath(path);
  const errors = useSubmitErrors(form.setError, pathOf);
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
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
      // The refusal lands on the last step: go back to the first step that holds a field it names.
      const fields = field ? [field] : known.map((f) => pathOf(f.path)).filter((p) => p !== undefined);
      const step = fields.map(stepOf).find((s) => s !== undefined);
      setReveal(step ? { step } : null);
    },
  });

  const e = form.formState.errors;
  const kind = form.watch('kind');
  const withOpening = form.watch('withOpening');
  const [moreOpen, setMoreOpen] = useState(false);

  return (
    <FormPage crumbs={[{ label: t('nav.investments'), to: '/investments' }]} title={t('investments_form.title')}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <StepForm
        trigger={(fields) => form.trigger(fields as Path<NewEnvelopeValues>[])}
        onSubmit={() => void form.handleSubmit((values) => { errors.clear(); create.mutate({ body: toCreateBody(values) }); })()}
        submitLabel={t('investments_form.submit')} pending={create.isPending} reveal={reveal}
        steps={[
          {
            id: 'kind', title: t('investments_form.step_kind'), fields: [...STEPS.kind],
            content: (
              <div className="grid gap-3 sm:grid-cols-2">
                <Controller control={form.control} name="kind" render={({ field }) => (
                  <Field label={t('investments_form.kind')} hint={t(`investments_form.kind_help.${field.value}`)} error={e.kind?.message}>
                    {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                      options={KINDS.map((k) => ({ value: k, label: t(`investments.kind.${k}`) }))} />}
                  </Field>
                )} />
                {kind === 'savings' ? (
                  <Controller control={form.control} name="regulated" render={({ field }) => (
                    <Field label={t('envelope.settings.regulated')} hint={t('envelope.settings.regulated_help')} error={e.regulated?.message}>
                      {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={regulatedOptions(t)} />}
                    </Field>
                  )} />
                ) : null}
                <Field label={t('investments_form.name')} error={e.displayName?.message}>
                  {(c) => <Input {...c} {...form.register('displayName')} />}
                </Field>
              </div>
            ),
          },
          {
            id: 'where', title: t('investments_form.step_where'), fields: [...STEPS.where],
            content: (
              <>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label={t('investments_form.institution')} hint={t('investments_form.institution_hint')} error={e.institution?.message}>
                    {(c) => <Input {...c} {...form.register('institution')} />}
                  </Field>
                  <Field label={t('investments_form.account_hint')} hint={t('investments_form.account_hint_help')} error={e.accountHint?.message}>
                    {(c) => <Input {...c} {...form.register('accountHint')} />}
                  </Field>
                </div>
                <OwnershipFields form={form} persons={persons} />
              </>
            ),
          },
          {
            id: 'value', title: t('investments_form.step_value'), fields: [...STEPS.value],
            content: (
              <>
                <p className="text-sm text-muted-foreground">{t('investments_form.value_hint')}</p>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label={t('investments_form.first_value_amount')} error={e.firstValueAmount?.message}>
                    {(c) => <Input inputMode="decimal" {...c} {...form.register('firstValueAmount')} />}
                  </Field>
                  <Controller control={form.control} name="firstValueOn" render={({ field }) => (
                    <Field label={t('investments_form.first_value_on')} error={e.firstValueOn?.message}>
                      {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
                    </Field>
                  )} />
                </div>
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
                <Fold label={t('investments_form.more')} open={moreOpen || !!e.currency || !!e.valuationCadence} onOpenChange={setMoreOpen}>
                  <Controller control={form.control} name="valuationCadence" render={({ field }) => (
                    <Field label={t('investments_form.cadence')} hint={t('investments_form.cadence_help')} error={e.valuationCadence?.message}>
                      {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                        options={CADENCES.map((cadence) => ({ value: cadence, label: t(`investments.cadence.${cadence}`) }))} />}
                    </Field>
                  )} />
                  <Controller control={form.control} name="currency" render={({ field }) => (
                    <Field label={t('investments_form.currency')} hint={t('investments_form.currency_help')} error={e.currency?.message}>
                      {(c) => <CurrencyField {...c} value={field.value} onValueChange={field.onChange} />}
                    </Field>
                  )} />
                </Fold>
              </>
            ),
          },
        ]}
      />
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
