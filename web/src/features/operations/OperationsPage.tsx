import { keepPreviousData } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { OperationsSearch } from '@/app/router';
import { OwnerChips } from '@/components/OwnerChips';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { Pager, useStepBack } from '@/components/page/Pager';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { OPERATIONS, type BankTransaction, type OperationPage } from '@/api/banking';
import { LineActions, lineState, SplitParts } from '../investments/operations/LineActions';
import { Receipts } from '../investments/operations/Receipts';
import { OperationFilters } from './OperationFilters';
import '@/locales/banks';

const CategoryDialog = lazy(() => import('../investments/operations/CategoryDialog'));
const NONE: ReadonlySet<string> = new Set();

/** The server answers a search of two characters or more; one typed character asks nothing yet. */
const MIN_SEARCH = 2;

/** « Opérations » (#84), a tab of Dépenses (#423): every bank line the reader may read, filtered and paged by the server; « À relier » is `?state=unlinked`. */
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
    max: search.max === undefined ? undefined : String(search.max), q: search.q && search.q.trim().length >= MIN_SEARCH ? search.q.trim() : undefined, page: search.page,
  };
  const answer = $api.useQuery('get', OPERATIONS, { params: { query } }, { placeholderData: keepPreviousData });
  // #426: the lines picked for « Catégoriser… », of the page shown: another page or filter starts with none.
  const shown = JSON.stringify(query);
  const [picked, setPicked] = useState<{ of: string; ids: ReadonlySet<string> }>({ of: shown, ids: NONE });
  const selected = picked.of === shown ? picked.ids : NONE;
  const [categorising, setCategorising] = useState(false);
  const toPage = (n: number) => go({ page: n > 1 ? n : undefined });
  useStepBack(page, answer.data && !answer.isPlaceholderData ? answer.data : undefined, toPage);
  const rows = answer.data?.items ?? [];
  const chosen = rows.filter((l) => selected.has(l.id));
  // The page's tab and Foyer / Moi filter no line.
  const onlyUnlinked = search.state === 'unlinked' && Object.entries(search).every(([k, v]) => v === undefined || ['state', 'page', 'tab', 'scope'].includes(k));
  const totals = answer.data?.totals;
  const money = (v: string) => formatCurrency(v, totals?.currency ?? 'EUR');
  return (
    <div className="space-y-4">
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
            <div className="flex min-h-9 flex-wrap items-center justify-between gap-2">
              <p className="text-sm text-muted-foreground">{t('banks.operations.count', { count: answer.data.total })}</p>
              {chosen.length > 0 ? <Button type="button" size="sm" onClick={() => setCategorising(true)}>{t('banks.operations.categorise_many', { count: chosen.length })}</Button> : null}
            </div>
            {rows.length === 0 ? <p className="text-muted-foreground">{t(onlyUnlinked ? 'banks.operations.to_link_empty' : 'banks.operations.no_match')}</p>
              : <div aria-busy={answer.isPlaceholderData} className={answer.isPlaceholderData ? 'opacity-60 transition-opacity' : undefined}>
                <Table rows={rows} owners={answer.data.accountOwners} selected={selected} onSelect={(ids) => setPicked({ of: shown, ids })} />
              </div>}
            <Pager page={page} total={answer.data.total} label={t('banks.operations.pages')} onPage={toPage} />
          </>
        )}
      {categorising && chosen.length > 0 ? (
        <Suspense fallback={null}>
          <CategoryDialog lines={chosen} onClose={() => setCategorising(false)} onDone={(message) => { setCategorising(false); setPicked({ of: shown, ids: NONE }); toast.success(message); }} />
        </Suspense>
      ) : null}
    </div>
  );
}

function Table({ rows, owners, selected, onSelect }: {
  rows: BankTransaction[]; owners: OperationPage['accountOwners']; selected: ReadonlySet<string>; onSelect: (ids: ReadonlySet<string>) => void;
}) {
  const { t } = useTranslation();
  // As « Catégoriser… » in a row's menu: a line of an account the reader writes, neither split nor ignored.
  const categorisable = (l: BankTransaction) => !!owners.find((a) => a.accountId === l.accountId)?.writable && !l.parts?.length && l.linkState !== 'ignored';
  const owner = (l: BankTransaction) => <OwnerChips shares={false} owners={(owners.find((a) => a.accountId === l.accountId)?.owners ?? []).map((o) => ({ personId: o.id, shareBp: 10_000 }))} />;
  const amount = (l: BankTransaction) => <span className="tabular-nums">{formatCurrency(l.amount, l.currency)}</span>;
  // A reader of an account they cannot write sees its lines, not the menu that edits them.
  const actions = (l: BankTransaction) => owners.find((a) => a.accountId === l.accountId)?.writable ? <LineActions line={l} onDone={toast.success} /> : null;
  const category = (l: BankTransaction) => l.parts?.length ? <SplitParts line={l} /> : l.category?.name ?? '—';
  const label = (l: BankTransaction) => (
    <span className="whitespace-normal">
      {l.label}<Receipts line={l} />
      {l.counterparty ? <span className="block text-sm text-muted-foreground">{l.counterparty}</span> : null}
      {l.note ? <span className="block text-sm italic text-muted-foreground">{l.note}</span> : null}
    </span>
  );
  return (
    <SortableTable<BankTransaction> caption={t('banks.operations.title')} rows={rows} rowKey={(l) => l.id}
      selection={{ selected, onChange: onSelect, label: (l) => t('banks.operations.select', { label: l.label }), allLabel: t('banks.operations.select_page'),
        disabled: (l) => !categorisable(l) }}
      columns={[
        { id: 'date', header: t('banks.operations.date'), cell: (l) => <LocalDate value={l.bookedOn} style="short" /> },
        { id: 'label', header: t('banks.operations.label'), cell: label },
        { id: 'account', header: t('banks.operations.account'), cell: (l) => l.accountName },
        { id: 'owner', header: t('banks.operations.owner'), cell: owner },
        { id: 'category', header: t('banks.operations.filter.category'), cell: category },
        { id: 'state', header: t('banks.operations.state'), cell: (l) => lineState(t, l) },
        { id: 'amount', header: t('banks.operations.amount'), align: 'end', cell: amount },
        { id: 'actions', header: t('banks.operations.actions_col'), align: 'end', cell: actions },
      ]}
      mobile={(l) => (
        <div className="space-y-1">
          <div className="flex items-start justify-between gap-2">{label(l)}{amount(l)}</div>
          <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">
            <span><LocalDate value={l.bookedOn} style="short" /> · {l.accountName} · {lineState(t, l)}</span>{owner(l)}
            {actions(l)}
          </div>
          <div className="text-sm">{category(l)}</div>
        </div>
      )} />
  );
}
