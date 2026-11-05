import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { FormProvider, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import type { RootSearch } from '@/app/router';
import { PageHeader } from '@/components/page/PageHeader';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { SALE_SCENARIOS, SCENARIOS } from '../api';
import { newSaleDraft, SaleFields, salePath, toSaleDocument, type SaleDraft } from './SaleFields';
import '@/locales/scenarios';

/** Issue #416 `/scenarios/new?kind=sale`: one short form; the figures are the detail page's once it is created. */
export function NewSaleScenarioPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const form = useForm<SaleDraft>({ defaultValues: newSaleDraft() });
  const errors = useSubmitErrors(form.setError, salePath);
  const create = $api.useMutation('post', SALE_SCENARIOS, {
    onSuccess: ({ id }) => {
      void queryClient.invalidateQueries({ queryKey: ['get', SCENARIOS] });
      void navigate({ to: '/scenarios/sale/$id', params: { id } });
    },
    onError: errors.onError,
  });

  return (
    <article className="space-y-4">
      <PageHeader crumbs={[{ label: t('scenarios.title'), to: '/scenarios' }]} title={t('scenarios.sale.title')} />
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <FormProvider {...form}>
        <form className="max-w-3xl space-y-4" noValidate onSubmit={(e) => void form.handleSubmit((v) => {
          errors.clear();
          create.mutate({ body: {
            name: v.name.trim(), visibility: v.visibility, scope, owners: [{ personId: session.person.id, shareBp: 10000 }], document: toSaleDocument(v),
          } });
        })(e)}>
          <SaleFields scope={scope} />
          <Button type="submit" disabled={create.isPending}>{t('scenarios.sale.submit')}</Button>
        </form>
      </FormProvider>
    </article>
  );
}
