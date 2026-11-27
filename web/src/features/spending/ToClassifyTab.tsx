import { useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { LINES_TO_CLASSIFY, presentBankError, type BankTransaction, type LineToClassify } from '@/api/banking';
import type { SpendingSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { useLineActions } from '../investments/operations/api';
import { basisTexts } from '../operations/suggestionBasis';
import '@/locales/banks';
import '@/locales/spending';

const CategoryDialog = lazy(() => import('../investments/operations/CategoryDialog'));

/**
 * « À classer »: the lines with no category on the accounts the person writes, newest first, each with the category Nid suggests (the
 * household's own choices, or labels near by meaning, #450). One click accepts a suggestion, « Autre… » picks another, and the page's
 * suggestions go in one action. The account and the page are in the URL and the server applies them; accepting is a hand choice, so the line
 * then counts for the next suggestions.
 */
export default function ToClassifyTab() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: SpendingSearch = useSearch({ strict: false });
  const account = search.account?.[0];
  const [page, toPage] = useListPage();
  const answer = $api.useQuery('get', LINES_TO_CLASSIFY, { params: { query: { ...(account ? { account } : {}), page } } });
  useStepBack(page, answer.data, toPage);
  const { categoriseMany } = useLineActions();
  const [picking, setPicking] = useState<BankTransaction | null>(null);
  const pick = (value: string) => void navigate({ to: '.', replace: true, search: (prev) => ({ ...prev, account: value ? [value] : undefined, page: undefined }) });

  // Every suggestion of the page, one call per category.
  const accept = async (items: LineToClassify[]) => {
    const byCategory = new Map<string, string[]>();
    for (const i of items) if (i.suggestion) byCategory.set(i.suggestion.categoryId, [...(byCategory.get(i.suggestion.categoryId) ?? []), i.line.id]);
    let done = 0;
    for (const [categoryId, transactionIds] of byCategory)
      done += (await categoriseMany.mutateAsync({ body: { transactionIds, categoryId } })).changed.length;
    toast.success(t('banks.to_classify.accepted', { count: done }));
  };

  const data = answer.data;
  const suggested = data?.items.filter((i) => i.suggestion) ?? [];
  const suggestion = (item: LineToClassify) => {
    const s = item.suggestion;
    if (!s) return <span className="text-muted-foreground">—</span>;
    const basis = basisTexts(s.basis);
    return (
      <span className="flex items-center gap-1.5">
        <span className="font-medium">{s.categoryName}</span>
        <InfoTip label={t('banks.to_classify.why', { category: s.categoryName })}>
          {t(basis.label, { count: s.evidenceCount })}. {t(basis.about, { agreement: s.agreement, category: s.categoryName })}
        </InfoTip>
      </span>
    );
  };
  const actions = (item: LineToClassify) => (
    <span className="flex justify-end gap-2">
      {item.suggestion ? (
        <Button type="button" size="sm" disabled={categoriseMany.isPending}
          aria-label={t('banks.to_classify.accept_one', { category: item.suggestion.categoryName, label: item.line.label })}
          onClick={() => categoriseMany.mutate({ body: { transactionIds: [item.line.id], categoryId: item.suggestion!.categoryId } }, {
            onSuccess: () => toast.success(t('banks.to_classify.accepted', { count: 1 })),
          })}>
          {t('banks.to_classify.accept')}
        </Button>
      ) : null}
      <Button type="button" size="sm" variant="outline" aria-label={t('banks.to_classify.other_for', { label: item.line.label })} onClick={() => setPicking(item.line)}>
        {item.suggestion ? t('banks.to_classify.other') : t('banks.to_classify.choose')}
      </Button>
    </span>
  );

  return (
    <div className="space-y-4">
      <p className="max-w-prose text-sm text-muted-foreground">{t('banks.to_classify.tip')}</p>
      <FilterRow label={t('common.filters')}>
        <FilterField label={t('banks.to_classify.account')} className="w-full sm:w-auto">
          {(labelId, controlId) => (
            <div className="min-w-0 flex-1 sm:w-56 sm:flex-none">
              <SelectField id={controlId} aria-labelledby={labelId} value={account ?? ''} onValueChange={pick}
                options={[{ value: '', label: t('banks.to_classify.all_accounts') }, ...(data?.accounts ?? []).map((a) => ({ value: a.id, label: a.name }))]} />
            </div>
          )}
        </FilterField>
      </FilterRow>
      {answer.error ? <ProblemMessage problem={presentBankError(t, answer.error)} /> : null}
      {categoriseMany.error ? <ProblemMessage problem={presentBankError(t, categoriseMany.error)} /> : null}
      {!data ? <Loading label={t('spending.loading')}><TableSkeleton columns={5} /></Loading> : data.total === 0 ? (
        <EmptyState module="/spending" title={t('banks.to_classify.empty')} body={t('banks.to_classify.empty_body')} />
      ) : (
        <>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="text-sm text-muted-foreground">{t('spending.to_classify', { count: data.total })}</p>
            {suggested.length > 0 ? (
              <Button type="button" size="sm" disabled={categoriseMany.isPending} onClick={() => void accept(suggested)}>
                {t('banks.to_classify.accept_page', { count: suggested.length })}
              </Button>
            ) : null}
          </div>
          <SortableTable<LineToClassify>
            caption={t('spending.tabs.to_classify')}
            rows={data.items}
            rowKey={(i) => i.line.id}
            columns={[
              { id: 'date', header: t('banks.to_classify.date'), cell: (i) => <LocalDate value={i.line.bookedOn} style="short" /> },
              { id: 'label', header: t('banks.to_classify.label'), wrap: true, cell: (i) => (
                <span><span className="block">{i.line.label}</span><span className="block text-sm text-muted-foreground">{i.line.accountName}</span></span>
              ) },
              { id: 'amount', header: t('banks.to_classify.amount'), align: 'end', cell: (i) => <span className="tabular-nums whitespace-nowrap">{formatCurrency(i.line.amount, i.line.currency)}</span> },
              { id: 'suggestion', header: t('banks.to_classify.suggestion'), wrap: true, cell: suggestion },
              { id: 'actions', header: t('banks.to_classify.actions'), align: 'end', cell: actions },
            ]}
            mobile={(i) => (
              <div className="space-y-1">
                <div className="flex justify-between gap-2"><span className="min-w-0 truncate">{i.line.label}</span><span className="tabular-nums whitespace-nowrap">{formatCurrency(i.line.amount, i.line.currency)}</span></div>
                <p className="text-sm text-muted-foreground"><LocalDate value={i.line.bookedOn} style="short" /> · {i.line.accountName}</p>
                <div className="text-sm">{suggestion(i)}</div>
                {actions(i)}
              </div>
            )}
          />
          <Pager page={page} total={data.total} onPage={toPage} />
        </>
      )}
      {picking ? (
        <Suspense fallback={null}>
          <CategoryDialog lines={[picking]} onClose={() => setPicking(null)} onDone={(message) => { setPicking(null); toast.success(message); }} />
        </Suspense>
      ) : null}
    </div>
  );
}
