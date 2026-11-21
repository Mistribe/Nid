import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { bankErrorText, presentBankError } from '@/api/banking';
import { FormDialog } from '@/components/FormDialog';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { InfoTip } from '@/components/InfoTip';
import { formatCurrency } from '@/components/Money';
import { Loading } from '@/components/page/skeletons';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { toFieldAmount, toWireAmount } from '@/lib/amount';
import { BUDGET_SUGGESTIONS, BUDGETS, type BudgetSuggestion } from './api';
import '@/locales/banks';
import '@/locales/spending';

/**
 * #427: « Proposer des budgets », for a household with none: the server's suggestions, each ticked or not as it says, its amount (the
 * Mois habituel rounded up) editable in place, or « Suivre mes engagements » where something is committed; the ticked ones in one POST.
 */
export function BudgetSuggestions({ currency, onClose, onDone }: { currency: string; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const suggestions = $api.useQuery('get', BUDGET_SUGGESTIONS);
  return (
    <FormDialog title={t('spending.budget.suggest_title')} className="sm:max-w-xl" onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => suggestions.error ? <ProblemMessage problem={presentBankError(t, suggestions.error)} />
        : !suggestions.data ? <Loading label={t('spending.budget.suggest_loading')}><Skeleton className="h-48 w-full" /></Loading>
        : suggestions.data.length === 0 ? (
          <div className="space-y-4">
            <p className="text-sm">{t('spending.budget.suggest_none')}</p>
            <div className="flex justify-end"><Button type="button" variant="outline" onClick={() => done()}>{t('common.close')}</Button></div>
          </div>
        )
        : <SuggestionForm suggestions={suggestions.data} currency={currency} titleId={titleId} done={done} />}
    </FormDialog>
  );
}

function SuggestionForm({ suggestions, currency, titleId, done }: {
  suggestions: BudgetSuggestion[]; currency: string; titleId: string; done: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The figures, the budgets and the suggestions all change.
  const post = $api.useMutation('post', BUDGETS, {
    onSuccess: () => { void queryClient.invalidateQueries({ predicate: (q) => typeof q.queryKey[1] === 'string' && q.queryKey[1].startsWith('/api/v1/banking/') }); },
  });
  const [rows, setRows] = useState(() => suggestions.map((s) => ({ ticked: s.preselected, amount: toFieldAmount(s.proposed) })));
  const set = (i: number, patch: Partial<(typeof rows)[number]>) => setRows((all) => all.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  // What is sent, in order: a field error's `budgets[k]` is the k-th ticked row.
  const ticked = rows.flatMap((r, i) => (r.ticked ? [i] : []));
  const fields = fieldErrorsOf(post.error);
  const fieldOf = (i: number) => fields.find((f) => f.path.startsWith(`budgets[${ticked.indexOf(i)}].`));
  const failure = post.error && fields.length === 0 ? post.error : null;
  const empty = ticked.some((i) => !suggestions[i]!.committed && !rows[i]!.amount.trim());

  return (
    <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
      event.preventDefault();
      event.stopPropagation();
      const budgets = ticked.map((i) => {
        const s = suggestions[i]!;
        return s.committed
          ? { categoryId: s.categoryId, source: 'committed', period: 'month' }
          : { categoryId: s.categoryId, amount: toWireAmount(rows[i]!.amount), source: 'typed', period: 'month' };
      });
      post.mutate({ body: { budgets } }, { onSuccess: () => done(t('spending.budget.suggest_created', { count: budgets.length })) });
    }}>
      <p className="text-sm text-muted-foreground">{t('spending.budget.suggest_intro')}</p>
      {failure ? <ProblemMessage problem={presentBankError(t, failure)} /> : null}
      <ul aria-label={t('spending.budget.suggest_list')} className="divide-y">
        {suggestions.map((s, i) => {
          const row = rows[i]!;
          const error = fieldOf(i);
          const errorId = `suggestion-${s.categoryId}-error`;
          return (
            <li key={s.categoryId} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2">
              <Checkbox id={`suggestion-${s.categoryId}`} checked={row.ticked} onCheckedChange={(checked) => set(i, { ticked: checked === true })} />
              <div className="min-w-0 flex-1">
                <label htmlFor={`suggestion-${s.categoryId}`} className="block font-medium">{s.name}</label>
                <p className="text-sm text-muted-foreground tabular-nums">{t('spending.budget.suggest_usual', { amount: formatCurrency(s.average, currency) })}</p>
              </div>
              {s.committed ? (
                <span className="inline-flex items-center gap-1 text-sm">
                  {t('spending.budget.follow')}<InfoTip label={t('spending.budget.follow')}>{t('spending.budget.follow_hint')}</InfoTip>
                </span>
              ) : (
                <Input inputMode="decimal" className="w-28 text-right tabular-nums" aria-label={t('spending.budget.suggest_amount', { name: s.name })}
                  disabled={!row.ticked} value={row.amount} onChange={(event) => set(i, { amount: event.target.value })}
                  aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined} />
              )}
              {error ? <p id={errorId} className="w-full text-sm text-destructive">{bankErrorText(t, error.code, error.message)}</p> : null}
            </li>
          );
        })}
      </ul>
      <div className="flex flex-wrap items-center justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={ticked.length === 0 || empty || post.isPending}>{t('spending.budget.suggest_create', { count: ticked.length })}</Button>
      </div>
    </form>
  );
}
