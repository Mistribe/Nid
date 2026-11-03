import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { PageHeader } from '@/components/page/PageHeader';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { LineActions } from '../investments/operations/LineActions';
import { UNLINKED_LINES, type BankTransaction, type BankTransactionPage } from '../investments/operations/api';
import '@/locales/banks';

/** Bank sync spec §6.5: the lines of every readable account still to link, newest first; the server refuses what the reader cannot write. */
export function UnlinkedPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const lines = $api.useInfiniteQuery('get', UNLINKED_LINES, { params: { query: {} } }, {
    pageParamName: 'page',
    initialPageParam: 1,
    getNextPageParam: (last: BankTransactionPage, pages: BankTransactionPage[]) => (last.hasMore ? pages.length + 1 : undefined),
  });
  const rows = lines.data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <div className="space-y-4">
      <PageHeader title={t('banks.operations.to_link')} />
      {lines.error ? <ProblemMessage problem={present(toProblem(lines.error))} /> : null}
      {lines.isPending ? <div role="status" aria-busy="true" aria-label={t('banks.operations.loading')} className="h-24 shimmer rounded-md border bg-muted" /> : null}
      {lines.data && rows.length === 0 ? <p className="text-muted-foreground">{t('banks.operations.to_link_empty')}</p> : null}
      {rows.length > 0 ? (
        <SortableTable<BankTransaction> caption={t('banks.operations.to_link')} rows={rows} rowKey={(l) => l.id}
          columns={[
            { id: 'date', header: t('banks.operations.date'), sort: (l) => l.bookedOn, cell: (l) => <LocalDate value={l.bookedOn} style="short" /> },
            { id: 'account', header: t('banks.operations.account'), sort: (l) => l.accountName, cell: (l) => l.accountName },
            { id: 'label', header: t('banks.operations.label'), sort: (l) => l.label, cell: (l) => <span className="whitespace-normal">{l.label}</span> },
            { id: 'amount', header: t('banks.operations.amount'), align: 'end', sort: (l) => amountKey(l.amount), cell: (l) => <span className="tabular-nums">{formatCurrency(l.amount, l.currency)}</span> },
            { id: 'actions', header: t('banks.operations.actions_col'), align: 'end', cell: (l) => <LineActions line={l} onDone={toast.success} /> },
          ]}
          mobile={(l) => (
            <div className="space-y-1">
              <div className="flex items-start justify-between gap-2"><span>{l.label}</span><span className="tabular-nums">{formatCurrency(l.amount, l.currency)}</span></div>
              <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">
                <span><LocalDate value={l.bookedOn} style="short" /> · {l.accountName}</span>
                <LineActions line={l} onDone={toast.success} />
              </div>
            </div>
          )} />
      ) : null}
      {lines.hasNextPage ? (
        <Button type="button" variant="outline" disabled={lines.isFetchingNextPage} onClick={() => void lines.fetchNextPage()}>{t('common.more')}</Button>
      ) : null}
    </div>
  );
}
