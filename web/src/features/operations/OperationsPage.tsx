import { keepPreviousData } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { OperationsSearch } from '@/app/router';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { Pager, useStepBack } from '@/components/page/Pager';
import { PageHeader } from '@/components/page/PageHeader';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { OPERATIONS, type BankTransaction } from '@/api/banking';
import { LineActions, lineState } from '../investments/operations/LineActions';
import { OperationFilters } from './OperationFilters';
import '@/locales/banks';

/** The server answers a search of two characters or more; one typed character asks nothing yet. */
const MIN_SEARCH = 2;

/** « Opérations » (#84): every bank line the reader may read, filtered and paged by the server; « À relier » is `?state=unlinked`. */
export function OperationsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: OperationsSearch = useSearch({ strict: false });
  const page = search.page ?? 1;
  const go = (change: Partial<OperationsSearch>) =>
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: OperationsSearch) => ({ ...prev, ...change })) as never });
  const query = {
    account: search.account, owner: search.owner, category: search.category, from: search.from, to: search.to, uncategorised: search.uncategorised,
    state: search.state, direction: search.direction, min: search.min === undefined ? undefined : String(search.min),
    max: search.max === undefined ? undefined : String(search.max), q: search.q && search.q.length >= MIN_SEARCH ? search.q : undefined, page: search.page,
  };
  const answer = $api.useQuery('get', OPERATIONS, { params: { query } }, { placeholderData: keepPreviousData });
  const toPage = (n: number) => go({ page: n > 1 ? n : undefined });
  useStepBack(page, answer.data && !answer.isPlaceholderData ? answer.data : undefined, toPage);
  const rows = answer.data?.items ?? [];
  const totals = answer.data?.totals;
  const money = (v: string) => formatCurrency(v, totals?.currency ?? 'EUR');
  return (
    <div className="space-y-4">
      <PageHeader title={t('banks.operations.title')} />
      <OperationFilters facets={answer.data?.facets} search={search} onChange={(change) => go({ ...change, page: undefined })} />
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data || !totals ? <Loading label={t('banks.operations.loading')}><TableSkeleton columns={6} /></Loading>
        : (
          <>
            <StatLine items={[
              { label: t('banks.operations.total_out'), value: money(totals.out) },
              { label: t('banks.operations.total_in'), value: money(totals.in) },
              { label: t('banks.operations.total_net'), value: money(totals.net), hint: totals.incomplete ? t('banks.operations.total_incomplete') : undefined },
            ]} />
            <p className="text-sm text-muted-foreground">{t('banks.operations.count', { count: answer.data.total })}</p>
            {rows.length === 0 ? <p className="text-muted-foreground">{t('banks.operations.no_match')}</p> : <Table rows={rows} />}
            <Pager page={page} total={answer.data.total} label={t('banks.operations.pages')} onPage={toPage} />
          </>
        )}
    </div>
  );
}

function Table({ rows }: { rows: BankTransaction[] }) {
  const { t } = useTranslation();
  const amount = (l: BankTransaction) => <span className="tabular-nums">{formatCurrency(l.amount, l.currency)}</span>;
  const actions = (l: BankTransaction) => <LineActions line={l} onDone={toast.success} />;
  const label = (l: BankTransaction) => (
    <span className="whitespace-normal">
      {l.label}
      {l.counterparty ? <span className="block text-sm text-muted-foreground">{l.counterparty}</span> : null}
    </span>
  );
  return (
    <SortableTable<BankTransaction> caption={t('banks.operations.title')} rows={rows} rowKey={(l) => l.id}
      columns={[
        { id: 'date', header: t('banks.operations.date'), cell: (l) => <LocalDate value={l.bookedOn} style="short" /> },
        { id: 'label', header: t('banks.operations.label'), cell: label },
        { id: 'account', header: t('banks.operations.account'), cell: (l) => l.accountName },
        { id: 'category', header: t('banks.operations.filter.category'), cell: (l) => l.category?.name ?? '—' },
        { id: 'state', header: t('banks.operations.state'), cell: (l) => lineState(t, l) },
        { id: 'amount', header: t('banks.operations.amount'), align: 'end', cell: amount },
        { id: 'actions', header: t('banks.operations.actions_col'), align: 'end', cell: actions },
      ]}
      mobile={(l) => (
        <div className="space-y-1">
          <div className="flex items-start justify-between gap-2">{label(l)}{amount(l)}</div>
          <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">
            <span><LocalDate value={l.bookedOn} style="short" /> · {l.accountName} · {lineState(t, l)}</span>
            {actions(l)}
          </div>
        </div>
      )} />
  );
}
