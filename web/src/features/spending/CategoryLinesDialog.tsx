import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { LocalDate } from '@/components/LocalDate';
import { ModalDialog } from '@/components/ModalDialog';
import { formatCurrency } from '@/components/Money';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import type { BankTransaction } from '@/api/banking';
import { LINES_PAGE, SPENDING_LINES, type SpendingLines, type SpendingQuery } from './api';
import '@/locales/spending';

/** The lines behind one figure of the page (a category and its children, or « Non classées »), newest first; the total is the server's. */
export function CategoryLinesDialog({ target, query, period, currency, onClose }: {
  target: { categoryId?: string; name: string }; query: SpendingQuery; period: string; currency: string; onClose: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const lines = $api.useInfiniteQuery('get', SPENDING_LINES, {
    params: { query: { ...query, ...(target.categoryId ? { categoryId: target.categoryId } : { uncategorised: true }) } },
  }, {
    pageParamName: 'page',
    initialPageParam: 1,
    getNextPageParam: (last: SpendingLines, pages: SpendingLines[]) => (pages.length * LINES_PAGE < last.count ? pages.length + 1 : undefined),
  });
  const first = lines.data?.pages[0];
  const rows = lines.data?.pages.flatMap((p) => p.items) ?? [];
  const amount = (l: BankTransaction) => <span className="tabular-nums">{formatCurrency(l.amount, l.currency)}</span>;

  return (
    <ModalDialog title={t('spending.lines.title', { name: target.name, period })} onClose={onClose} className="sm:max-w-3xl">
      {() => (
        <div className="space-y-3">
          {lines.error ? <ProblemMessage problem={present(toProblem(lines.error))} /> : null}
          {lines.isPending ? <Loading label={t('spending.lines.loading')}><TableSkeleton columns={4} /></Loading> : null}
          {first ? (
            <p className="flex flex-wrap items-baseline gap-x-3">
              <span className="text-muted-foreground">{t('spending.lines.total')}</span>
              <span className="text-figure">{formatCurrency(first.total, currency)}</span>
              <span className="text-sm text-muted-foreground">{t('spending.lines.count', { count: first.count })}</span>
            </p>
          ) : null}
          {first?.incomplete ? <Incomplete /> : null}
          {first && rows.length === 0 ? <p className="text-muted-foreground">{t('spending.lines.empty')}</p> : null}
          {rows.length > 0 ? (
            <SortableTable<BankTransaction> caption={t('spending.lines.list')} rows={rows} rowKey={(l) => l.id}
              columns={[
                { id: 'date', header: t('spending.lines.date'), cell: (l) => <LocalDate value={l.bookedOn} style="short" /> },
                { id: 'label', header: t('spending.lines.label'), wrap: true, cell: (l) => l.label },
                { id: 'account', header: t('spending.lines.account'), cell: (l) => l.accountName },
                { id: 'amount', header: t('spending.lines.amount'), align: 'end', cell: amount },
              ]}
              mobile={(l) => (
                <div className="space-y-1">
                  <div className="flex items-start justify-between gap-2"><span>{l.label}</span>{amount(l)}</div>
                  <div className="text-sm text-muted-foreground"><LocalDate value={l.bookedOn} style="short" /> · {l.accountName}</div>
                </div>
              )} />
          ) : null}
          {lines.hasNextPage ? (
            <Button type="button" variant="outline" disabled={lines.isFetchingNextPage} onClick={() => void lines.fetchNextPage()}>{t('spending.lines.more')}</Button>
          ) : null}
        </div>
      )}
    </ModalDialog>
  );
}

/** The server left out a foreign line it had no rate for. */
export function Incomplete({ text }: { text?: string }) {
  const { t } = useTranslation();
  return (
    <p role="note" className="flex items-center gap-2 text-sm">
      <AlertTriangle aria-hidden="true" className="size-4 shrink-0 text-status-warning" />{text ?? t('spending.incomplete')}
    </p>
  );
}
