import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { aiProblem, BUDGET, fieldError } from '@/api/ai';
import { Field } from '@/components/form/Field';
import { InfoTip } from '@/components/InfoTip';
import { formatCurrency } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import '@/locales/ai';

/**
 * « Budget » (#488, docs/ingestion/llm-providers.md §7): the household's monthly ceiling for paid cloud providers, which any
 * adult sets (0 € by default: no paid call), and this month's spend as the server counts it. The household's workers are
 * never budgeted.
 */
export function BudgetSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const budget = $api.useQuery('get', BUDGET);
  const [draft, setDraft] = useState<string | null>(null);
  const save = $api.useMutation('put', BUDGET, {
    onSuccess: () => {
      setDraft(null);
      toast.success(t('ai.budget.saved'));
      void queryClient.invalidateQueries({ queryKey: ['get', BUDGET] });
    },
  });
  const eur = (amount: string) => formatCurrency(amount, 'EUR');
  const amountError = fieldError(t, save.error, 'monthlyBudgetEur', t('ai.budget.required'));
  const view = budget.data;

  return (
    <Section title={t('ai.budget.title')} tip={<InfoTip label={t('ai.budget.title')}>{t('ai.budget.tip')}</InfoTip>}
      actions={<Link to="/settings/ai/usage" className="text-sm font-medium underline underline-offset-4">{t('ai.usage.link')}</Link>}>
      {budget.error ? <ProblemMessage problem={aiProblem(t, budget.error)} /> : null}
      {save.error && !amountError ? <ProblemMessage problem={aiProblem(t, save.error)} /> : null}
      {view ? (
        <>
          <p className="text-sm">
            {view.status === 'off' ? t('ai.budget.off')
              : t('ai.budget.spent', { spent: eur(view.spentEur), cap: eur(view.monthlyBudgetEur), remaining: eur(view.remainingEur) })}
          </p>
          {view.status === 'alert' ? <p role="status" className="rounded-md bg-status-warning/10 px-3 py-2 text-sm text-status-warning-text">{t('ai.budget.alert')}</p> : null}
          {view.status === 'reached' ? <p role="status" className="rounded-md bg-status-warning/10 px-3 py-2 text-sm text-status-warning-text">{t('ai.budget.reached')}</p> : null}
          <form noValidate className="flex max-w-sm flex-wrap items-end gap-2" onSubmit={(event) => {
            event.preventDefault();
            save.mutate({ body: { monthlyBudgetEur: (draft ?? view.monthlyBudgetEur).replace(',', '.').trim() } });
          }}>
            <div className="min-w-0 flex-1">
              <Field label={t('ai.budget.cap')} hint={t('ai.budget.cap_hint')} error={amountError}>
                {(c) => <Input {...c} inputMode="decimal" value={draft ?? view.monthlyBudgetEur} onChange={(e) => setDraft(e.target.value)} />}
              </Field>
            </div>
            <Button type="submit" disabled={save.isPending}>{t('ai.budget.save')}</Button>
          </form>
        </>
      ) : null}
    </Section>
  );
}
