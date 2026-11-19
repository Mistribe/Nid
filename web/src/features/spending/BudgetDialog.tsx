import { useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { bankErrorText, presentBankError } from '@/api/banking';
import { ConfirmAction } from '@/components/ConfirmAction';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { Segmented } from '@/components/form/Segmented';
import { Loading } from '@/components/page/skeletons';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { toFieldAmount, toWireAmount } from '@/lib/amount';
import { BUDGET, BUDGETS, type SpendingBudget } from './api';
import '@/locales/banks';
import '@/locales/spending';

export type BudgetTarget = { id: string; name: string; budget?: SpendingBudget | null };

/**
 * #85: a category's budget, typed per month or per year; « Suivre mes engagements » leaves the amount to what is committed there
 * (subscriptions, loans, childcare). The household's budgets say whether one exists, the spending figures whether anything is committed.
 */
export function BudgetDialog({ target, onClose, onDone }: { target: BudgetTarget; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const budgets = $api.useQuery('get', BUDGETS);
  return (
    <FormDialog title={t('spending.budget.title', { name: target.name })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => budgets.error ? <ProblemMessage problem={presentBankError(t, budgets.error)} />
        : !budgets.data ? <Loading label={t('spending.budget.loading')}><Skeleton className="h-24 w-full" /></Loading>
        : <BudgetForm target={target} existing={budgets.data.find((b) => b.categoryId === target.id)} titleId={titleId} done={done} />}
    </FormDialog>
  );
}

function BudgetForm({ target, existing, titleId, done }: {
  target: BudgetTarget; existing?: { amount?: string | null; period: string }; titleId: string; done: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The figures and the budgets both change.
  const onSuccess = () => { void queryClient.invalidateQueries({ predicate: (q) => typeof q.queryKey[1] === 'string' && q.queryKey[1].startsWith('/api/v1/banking/') }); };
  const put = $api.useMutation('put', BUDGET, { onSuccess });
  const remove = $api.useMutation('delete', BUDGET, { onSuccess });
  const [amount, setAmount] = useState(existing?.amount ? toFieldAmount(existing.amount) : '');
  const [period, setPeriod] = useState(existing?.period === 'year' ? 'year' : 'month');
  const periodId = useId();
  const path = { categoryId: target.id };

  const field = fieldErrorsOf(put.error).find((f) => f.path === 'amount');
  const failure = (put.error && !field) ? put.error : remove.error;
  // Following is offered when something is committed there and the budget does not follow it already.
  const canFollow = !!target.budget?.committed && !(existing && !existing.amount);
  const save = (value: string | null, message: string) => put.mutate({ params: { path }, body: { amount: value, period } }, { onSuccess: () => done(message) });

  return (
    <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
      event.preventDefault();
      event.stopPropagation();
      save(toWireAmount(amount), t('spending.budget.saved'));
    }}>
      {failure ? <ProblemMessage problem={presentBankError(t, failure)} /> : null}
      <Field label={t('spending.budget.amount')} hint={t(period === 'year' ? 'spending.budget.amount_hint_year' : 'spending.budget.amount_hint_month')}
        error={field ? bankErrorText(t, field.code, field.message) : undefined}>
        {(c) => <Input inputMode="decimal" {...c} value={amount} onChange={(event) => setAmount(event.target.value)} />}
      </Field>
      <div className="space-y-1">
        <p id={periodId} className="text-sm">{t('spending.budget.period')}</p>
        <Segmented aria-labelledby={periodId} value={period} onValueChange={setPeriod} options={[
          { value: 'month', label: t('spending.budget.monthly') },
          { value: 'year', label: t('spending.budget.yearly_option') },
        ]} />
      </div>
      {canFollow ? (
        <div className="space-y-1">
          <Button type="button" variant="outline" size="sm" disabled={put.isPending} onClick={() => save(null, t('spending.budget.following'))}>
            {t('spending.budget.follow')}
          </Button>
          <p className="text-sm text-muted-foreground">{t('spending.budget.follow_hint')}</p>
        </div>
      ) : null}
      <div className="flex flex-wrap items-center justify-end gap-2">
        {existing ? (
          <span className="mr-auto">
            <ConfirmAction label={t('spending.budget.delete')} confirmation={t('spending.budget.delete_confirm', { name: target.name })} pending={remove.isPending}
              onConfirm={() => remove.mutate({ params: { path } }, { onSuccess: () => done(t('spending.budget.deleted')) })} />
          </span>
        ) : null}
        <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={!amount.trim() || put.isPending}>{t('spending.budget.save')}</Button>
      </div>
    </form>
  );
}
