import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useMemo, useState, type ReactNode } from 'react';
import { useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { ProblemMessage } from '@/components/ProblemMessage';
import { PageHeader } from '@/components/page/PageHeader';
import { StepForm } from '@/components/page/StepForm';
import { invalidateSubscriptions, LIST, PERSONS } from './api';
import { useSubmitErrors } from './edits/useSubmitErrors';
import { fieldErrorsOf } from './fieldErrors';
import { stepOfPath, stepsFor, thenMode, type Offer, type StepName } from './wizard/offers';
import { Bundle } from './wizard/offers/Bundle';
import type { Form, Person } from './wizard/offers/fields';
import { Gift } from './wizard/offers/Gift';
import { Prepaid } from './wizard/offers/Prepaid';
import { leaveTrial, Trial } from './wizard/offers/Trial';
import { fieldOf, toCreateBody, wizardSchema, type WizardValues } from './wizard/schema';
import { CommonStep, fillFollowingStart, RecurringOffer, SummaryStep } from './wizard/steps';
import { ThenStep } from './wizard/ThenStep';
import { TypeStep } from './wizard/TypeStep';

type Fields = Path<WizardValues>[];
const PURCHASE: Fields = ['offerDate', 'offerLabel', 'offerAmount', 'currency', 'offerPayerType', 'offerPayerPerson'];
const PHASE: Fields = ['amount', 'currency', 'everyCount', 'everyUnit', 'sameCovers', 'coversCount', 'coversUnit', 'maxCycles', 'payerType', 'payerPerson'];

/** The fields the offer step checks before going on, by offer (1E2b1 design §4.1); never a field the current
 * choice hides (final review, finding 1), so a stale value left there cannot block "Continuer". */
function offerFields(v: WizardValues): Fields {
  switch (v.offer) {
    case 'recurring': return ['startsOn', ...PHASE];
    case 'prepaid': return [...PURCHASE, 'paidCount', 'paidUnit', 'bonusCount', 'bonusUnit'];
    case 'trial': return v.trialVariant === 'free'
      ? ['trialVariant', 'offerDate', 'durationCount', 'durationUnit']
      : ['trialVariant', 'offerDate', 'launchAmount', 'currency', 'everyCount', 'everyUnit', 'launchCycles'];
    case 'gift': {
      const base: Fields = ['offerDate', 'offerLabel', 'giftReceived', 'durationCount', 'durationUnit'];
      return v.giftReceived ? base : [...base, 'offerAmount', 'currency', 'offerPayerType', 'offerPayerPerson'];
    }
    case 'bundle': return [...PURCHASE, 'allocated', 'durationCount', 'durationUnit', 'others'];
  }
}

const stepFields = (name: StepName, v: WizardValues): Fields => {
  switch (name) {
    case 'type': return ['offer'];
    case 'common': return ['provider', 'displayName', 'visibility', 'owners', 'users', 'note'];
    case 'offer': return offerFields(v);
    case 'then': {
      const mode = thenMode(v);
      if (mode === 'none') return [];
      if (mode === 'required') return ['nextStartsOn', ...PHASE];
      return v.then ? ['then', 'startsOn', ...PHASE] : ['then'];
    }
    case 'summary': return [];
  }
};

/** 1E2a design §5, 1E2b1 design §4.1: the offer type, the subscription, the offer, what follows it, sent as one request at the end. */
export function NewSubscription() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
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
      offer: 'recurring', trialVariant: 'free', offerDate: '', offerLabel: '', offerAmount: '', offerPayerType: 'household', offerPayerPerson: '',
      paidCount: '12', paidUnit: 'month', bonusCount: '', bonusUnit: 'month', durationCount: '1', durationUnit: 'month', giftReceived: true,
      allocated: '0', launchAmount: '', launchCycles: '', nextStartsOn: '', others: [], then: false,
    },
  });
  const errors = useSubmitErrors(form.setError, (path) => fieldOf(path, form.getValues()));
  const create = $api.useMutation('post', LIST, {
    onSuccess: (created) => {
      void invalidateSubscriptions(queryClient);
      void navigate({ to: '/subscriptions/$id', params: { id: created.id } });
    },
    onError: (error) => {
      errors.onError(error);
      // The refusal lands on the last step: go back to the first step that holds a field it names.
      const { offer, trialVariant } = form.getValues();
      const owned = fieldErrorsOf(error)
        .map((f) => stepOfPath(offer, trialVariant, f.path))
        .filter((s): s is StepName => s !== undefined)
        .map((s) => stepsFor(offer).indexOf(s))
        .filter((i) => i >= 0);
      setReveal(owned.length > 0 ? { step: stepsFor(offer)[Math.min(...owned)]! } : null);
    },
  });

  const values = form.watch();
  const offer = values.offer;
  const titles: Record<StepName, string> = {
    type: t('wizard.offer.legend'), common: t('wizard.step_common'), offer: t('wizard.step_offer'),
    then: t('wizard.then.title'), summary: t('wizard.step_summary'),
  };
  const content: Record<StepName, ReactNode> = {
    type: <TypeStep form={form} />,
    common: <CommonStep form={form} persons={persons} />,
    offer: <OfferForm form={form} persons={persons} offer={offer} />,
    then: <ThenStep form={form} persons={persons} />,
    summary: <SummaryStep form={form} persons={persons} />,
  };

  return (
    <article className="max-w-3xl space-y-4">
      <PageHeader crumbs={[{ label: t('nav.subscriptions'), to: '/subscriptions' }]} title={t('wizard.title')} />
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      {people.error ? <ProblemMessage problem={present(toProblem(people.error))} /> : null}
      {/* The owner and payer selects are born with their options: a select registered before them would show its
          first option while the form holds the reader (final review). */}
      {!people.data ? <div aria-busy="true" aria-label={t('wizard.loading')} className="h-40 shimmer rounded-md border bg-muted" /> : (
        <StepForm
          steps={stepsFor(offer).map((id) => ({ id, title: titles[id], fields: stepFields(id, values), content: content[id] }))}
          trigger={(fields) => form.trigger(fields as Path<WizardValues>[])}
          onAdvance={(id) => {
            if (id !== 'offer') return;
            if (offer === 'trial') leaveTrial(form, t('wizard.offer.trial_label'));
            if (thenMode(form.getValues()) === 'optional') fillFollowingStart(form);
          }}
          onSubmit={() => void form.handleSubmit((v) => { errors.clear(); create.mutate({ body: toCreateBody(v) }); })()}
          submitLabel={t('wizard.submit')} pending={create.isPending} reveal={reveal} />
      )}
    </article>
  );
}

function OfferForm({ form, persons, offer }: { form: Form; persons: Person[]; offer: Offer }) {
  switch (offer) {
    case 'recurring': return <RecurringOffer form={form} persons={persons} />;
    case 'prepaid': return <Prepaid form={form} persons={persons} />;
    case 'trial': return <Trial form={form} />;
    case 'gift': return <Gift form={form} persons={persons} />;
    case 'bundle': return <Bundle form={form} persons={persons} />;
  }
}
