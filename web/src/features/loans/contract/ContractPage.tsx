import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { FormProvider, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate } from '@/components/LocalDate';
import { ErrorPage } from '@/components/ErrorPage';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { FormActions, FormPage } from '@/components/page/FormPage';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { fieldErrorsOf } from '../../subscriptions/fieldErrors';
import { CONTRACT, invalidateLoans, LOAN, type LoanDetail } from '../api';
import { money } from '../format';
import { ContractFields } from '../new/ContractFields';
import { contractPath, contractSchema, toContractBody, type ContractValues } from '../new/loanForm';
import { presentRefusal } from '../refusal';
import { contractValuesOf, isPlain, repaymentRank } from './contractValues';
import '@/locales/loans';

/** 3B2 design §5: the contract edited on its own page, sent whole with the version it was read at. */
export function ContractPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/loans/$id/contract' });
  const answer = $api.useQuery('get', LOAN, { params: { path: { id } } });
  // « Recharger » remounts the form on the contract read again; a background refetch never resets what is being typed.
  const [generation, setGeneration] = useState(0);
  // The loan's crumb waits for its name.
  const crumbs = (name?: string) => [{ label: t('nav.loans'), to: '/loans' as const }, ...(name ? [{ label: name, to: '/loans/$id' as const, params: { id } }] : [])];
  const page = (name: string | undefined, children: ReactNode, title = t('loan_contract.form')) => <FormPage crumbs={crumbs(name)} title={title} width="max-w-2xl">{children}</FormPage>;

  // An unknown loan and another adult's private one both answer 404: the same « Page introuvable », under « Crédits ».
  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs()} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return page(undefined, <div aria-busy="true" aria-label={t('loan_contract.loading')} className="h-40 shimmer rounded-md border bg-muted" />);

  const loan = answer.data;
  return page(loan.name, loan.canWrite
    ? <ContractForm key={generation} loan={loan} onReload={() => answer.refetch().then(() => setGeneration((g) => g + 1))} />
    : <p>{t('loan_detail.read_only')}</p>, t('loan_contract.title', { name: loan.name }));
}

function ContractForm({ loan, onReload }: { loan: LoanDetail; onReload: () => Promise<void> }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  // The loan the form opened on: the version it sends and the repayments a refusal's rank names, whatever a refetch brings.
  const [opened] = useState(loan);
  const form = useForm<ContractValues>({ resolver: zodResolver(contractSchema(t)), defaultValues: contractValuesOf(opened.contract) });
  const errors = useSubmitErrors(form.setError, contractPath);
  const [impossible, setImpossible] = useState<string | null>(null);
  const save = $api.useMutation('put', CONTRACT, {
    onSuccess: () => {
      void invalidateLoans(queryClient);
      void navigate({ to: '/loans/$id', params: { id: opened.id }, state: { loanSaved: 'contract' } });
    },
    onError: (error) => {
      // A stored repayment the new contract makes impossible is named at the top, from the detail's list (3B2 design §5).
      const rank = fieldErrorsOf(error).map((f) => repaymentRank(f.path)).find((i) => i !== null);
      const repayment = rank == null ? undefined : opened.repayments[rank];
      if (repayment)
        setImpossible(t('loan_contract.repayment_impossible', { date: formatLocalDate(repayment.onDate), amount: money(opened.currency, repayment.amount) }));
      else errors.onError(error);
    },
  });
  const problem = errors.problem ? presentRefusal(t, save.error) : null;

  return (
    <FormProvider {...form}>
      <form aria-label={t('loan_contract.form')} noValidate className="space-y-4"
        onSubmit={(ev) => void form.handleSubmit((values) => {
          errors.clear();
          setImpossible(null);
          save.mutate({ params: { path: { id: opened.id } }, body: { version: opened.contract.version, ...toContractBody(values) } });
        })(ev)}>
        {impossible ? <ProblemMessage problem={{ kind: 'validation', detail: impossible }} /> : null}
        {problem ? (
          <>
            <ProblemMessage problem={problem} />
            {problem.kind === 'conflict' ? (
              <>
                <p>{t('loan_contract.reload_loses')}</p>
                <Reload onReload={onReload} />
              </>
            ) : null}
          </>
        ) : null}
        <ContractFields stored startListed={!isPlain(opened.contract)} />
        <FormActions><Button type="submit" disabled={save.isPending}>{t('loan_contract.submit')}</Button></FormActions>
      </form>
    </FormProvider>
  );
}
