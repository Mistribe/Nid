import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from '@tanstack/react-router';
import { useState } from 'react';
import { FormProvider, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { fieldErrorsOf } from '../../subscriptions/fieldErrors';
import { CONTRACT, invalidateLoans, LOAN, type LoanDetail } from '../api';
import { money } from '../format';
import { ContractFields } from '../new/ContractFields';
import { contractPath, contractSchema, toContractBody, type ContractValues } from '../new/loanForm';
import { presentRefusal } from '../refusal';
import { contractValuesOf, isPlain, repaymentRank } from './contractValues';

/** 3B2 design §5: the contract edited on its own page, sent whole with the version it was read at. */
export function ContractPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/loans/$id/contract' });
  const answer = $api.useQuery('get', LOAN, { params: { path: { id } } });
  // « Recharger » remounts the form on the contract read again; a background refetch never resets what is being typed.
  const [generation, setGeneration] = useState(0);
  const back = <Link to="/loans/$id" params={{ id }} className="text-sm underline">{t('loan_contract.back')}</Link>;

  // An unknown loan and another adult's private one both answer 404: « Cet élément est introuvable. ».
  if (answer.error) return <>{back}<ProblemMessage problem={present(toProblem(answer.error))} /></>;
  if (!answer.data) return <>{back}<div aria-busy="true" aria-label={t('loan_contract.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></>;

  const loan = answer.data;
  return (
    <article className="max-w-2xl space-y-4">
      {back}
      <h1 className="text-2xl font-semibold">{t('loan_contract.title', { name: loan.name })}</h1>
      {loan.canWrite
        ? <ContractForm key={generation} loan={loan} onReload={() => answer.refetch().then(() => setGeneration((g) => g + 1))} />
        : <p>{t('loan_detail.read_only')}</p>}
    </article>
  );
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
        <Button type="submit" disabled={save.isPending}>{t('loan_contract.submit')}</Button>
      </form>
    </FormProvider>
  );
}
