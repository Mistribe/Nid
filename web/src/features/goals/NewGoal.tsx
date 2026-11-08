import { zodResolver } from '@hookform/resolvers/zod';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm, type Path, type Resolver } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toProblem } from '@/api/errors';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import type { NewGoalSearch } from '@/app/router';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { FormPage } from '@/components/page/FormPage';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { PERSONS } from '@/api/persons';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { useCreateGoal } from './api';
import { FundingFields, useEnvelopeOptions } from './FundingFields';
import { GoalFields } from './GoalFields';
import { goalDefaults, goalPath, goalSchema, toCreateBody, type GoalValues } from './goalSchema';
import { useGoalErrors } from './useGoalErrors';
import '@/locales/goals';

const STEPS = {
  goal: ['kind', 'name', 'amount', 'months', 'targetDate', 'note'],
  funding: ['mode', 'funding'],
  owners: ['visibility', 'owners'],
} as const;

/** A goal in three steps and one request: what it is, what funds it, who owns it. */
export function NewGoal() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const options = useEnvelopeOptions();
  // Issue #412: opened from the empty state as an emergency fund, its kind, months and name already chosen.
  const search: NewGoalSearch = useSearch({ strict: false });
  const preset = search.kind === 'emergency_fund'
    ? { kind: search.kind, name: t('goals_list.kind.emergency_fund'), ...(search.months ? { months: String(search.months) } : {}) }
    : {};
  const form = useForm<GoalValues>({
    resolver: zodResolver(goalSchema(t, { withOwners: true })) as unknown as Resolver<GoalValues>,
    defaultValues: { ...goalDefaults(), ...preset, visibility: 'shared', owners: [{ personId: session.person.id, share: '100' }] },
  });
  const errors = useGoalErrors(form.setError);
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
  const create = useCreateGoal();

  const onError = (error: unknown) => {
    errors.onError(error);
    // The refusal lands on the last step: go back to the first step that holds a field it names.
    const roots = toProblem(error).code === 'goals.share_exceeded' ? ['funding'] : fieldErrorsOf(error).map((f) => goalPath(f.path)?.split('.')[0]);
    const step = (Object.keys(STEPS) as (keyof typeof STEPS)[]).find((id) => roots.some((r) => (STEPS[id] as readonly (string | undefined)[]).includes(r)));
    setReveal(step ? { step } : null);
  };

  return (
    <FormPage crumbs={[{ label: t('goals_list.title'), to: '/goals' }]} title={t('goals_form.title')}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <StepForm
        trigger={(fields) => form.trigger(fields as Path<GoalValues>[])}
        onSubmit={() => void form.handleSubmit((values) => {
          errors.clear();
          create.mutate({ body: toCreateBody(values) }, {
            onSuccess: ({ id }) => void navigate({ to: '/goals/$id', params: { id } }),
            onError,
          });
        })()}
        submitLabel={t('goals_form.submit')} pending={create.isPending} reveal={reveal}
        steps={[
          { id: 'goal', title: t('goals_form.step_goal'), fields: [...STEPS.goal], content: <GoalFields form={form} withKind /> },
          { id: 'funding', title: t('goals_form.step_funding'), fields: [...STEPS.funding], content: <FundingFields form={form} options={options} /> },
          { id: 'owners', title: t('goals_form.step_owners'), fields: [...STEPS.owners], content: <OwnershipFields form={form} persons={people.data ?? []} /> },
        ]}
      />
    </FormPage>
  );
}
