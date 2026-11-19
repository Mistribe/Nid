import { zodResolver } from '@hookform/resolvers/zod';
import { useNavigate } from '@tanstack/react-router';
import { useForm, type Resolver } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { PERSONS } from '@/api/persons';
import { useSession } from '@/app/Providers';
import { Field } from '@/components/form/Field';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { checkOwnership, ownershipPath, ownershipShape, ownershipToWire, type OwnershipValues } from '@/components/ownership/ownership';
import { FormActions, FormPage } from '@/components/page/FormPage';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useCreateNanny } from './nannyApi';
import '@/locales/childcare';
import '@/locales/forms';

type Values = OwnershipValues & { name: string };

/** #94: a nanny at home by its name and its owners; its months are recorded on its page, one at a time. */
export function NewNanny() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const navigate = useNavigate();
  const people = $api.useQuery('get', PERSONS);
  const schema = z.object({ name: z.string().trim().min(1, t('edit.required')).max(120), ...ownershipShape(t) })
    .superRefine((v, ctx) => checkOwnership(v, ctx, t));
  const form = useForm<Values>({
    resolver: zodResolver(schema) as unknown as Resolver<Values>,
    defaultValues: { name: t('childcare_nanny.default_name'), visibility: 'shared', owners: [{ personId: session.person.id, share: '100' }] },
  });
  const errors = useSubmitErrors(form.setError, (path) => (path === 'name' ? 'name' : ownershipPath(path)));
  const create = useCreateNanny();

  return (
    <FormPage crumbs={[{ label: t('childcare_list.title'), to: '/childcare' }]} title={t('childcare_nanny.new_title')} width="max-w-2xl">
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <form noValidate className="space-y-4" onSubmit={(e) => void form.handleSubmit((values) => {
        errors.clear();
        create.mutate({ body: { name: values.name.trim(), ...ownershipToWire(values) } }, {
          onSuccess: ({ id }) => void navigate({ to: '/childcare/nannies/$id', params: { id } }),
          onError: errors.onError,
        });
      })(e)}>
        <p className="text-muted-foreground">{t('childcare_nanny.new_intro')}</p>
        <Field label={t('childcare_nanny.name')} hint={t('childcare_nanny.name_hint')} error={form.formState.errors.name?.message}>
          {(c) => <Input {...c} {...form.register('name')} />}
        </Field>
        <OwnershipFields form={form} persons={people.data ?? []} />
        <FormActions>
          <Button type="submit" disabled={create.isPending}>{t('childcare_nanny.create')}</Button>
        </FormActions>
      </form>
    </FormPage>
  );
}
