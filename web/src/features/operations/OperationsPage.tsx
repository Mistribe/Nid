import { keepPreviousData } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { OperationsSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { OwnerChips } from '@/components/OwnerChips';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { Pager, useStepBack } from '@/components/page/Pager';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { OPERATIONS, type BankTransaction, type OperationPage } from '@/api/banking';
import { LineActions, lineState, SplitParts } from '../investments/operations/LineActions';
import { Receipts } from '../investments/operations/Receipts';
import { CategorySuggestions } from './CategorySuggestions';
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
  const search: OperationsSearch & RootSearch = useSearch({ strict: false });
  const page = search.page ?? 1;
  const go = (change: Partial<OperationsSearch>) =>
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: OperationsSearch) => ({ ...prev, ...change })) as never });
  const query = {
    account: search.account, owner: search.owner, category: search.category, from: search.from, to: search.to, uncategorised: search.uncategorised,
    state: search.state, direction: search.direction, min: search.min === undefined ? undefined : String(search.min),
    max: search.max === undefined ? undefined : String(search.max), q: search.q && search.q.trim().length >= MIN_SEARCH ? search.q.trim() : undefined, page: search.page,
    // #437: the lines a figure of Dépenses counts are those of its Foyer / Moi accounts; any other state lists every account read.
    scope: search.state === 'counted' ? (search.scope === 'me' ? 'me' : 'household') : undefined,
  };
  const answer = $api.useQuery('get', OPERATIONS, { params: { query } }, { placeholderData: keepPreviousData });
  // #426: the lines picked for « Catégoriser… », of the page shown: another page or filter starts with none.
  const shown = JSON.stringify(query);
  const [picked, setPicked] = useState<{ of: string; ids: ReadonlySet<string> }>({ of: shown, ids: NONE });
  const selected = picked.of === shown ? picked.ids : NONE;
  const [categorising, setCategorising] = useState(false);
  // Where the focus goes once a suggestion's write leaves none: the list's count, just above it.
  const count = useRef<HTMLParagraphElement>(null);
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
      <CategorySuggestions emptied={count} />
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
              <p ref={count} tabIndex={-1} className="text-sm text-muted-foreground outline-none">{t('banks.operations.count', { count: answer.data.total })}</p>
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
  // A credit reads in the « good » colour; the server's sign, read off its text.
  const amount = (l: BankTransaction) => (
    <span className={cn('font-medium whitespace-nowrap tabular-nums', !l.amount.startsWith('-') && 'text-status-good-text')}>{formatCurrency(l.amount, l.currency)}</span>
  );
  // A reader of an account they cannot write sees its lines, not the menu that edits them.
  const actions = (l: BankTransaction) => owners.find((a) => a.accountId === l.accountId)?.writable ? <LineActions line={l} onDone={toast.success} /> : null;
  // A line still to classify says so, as the tab's count does.
  const toClassify = (l: BankTransaction) => !l.category && !l.parts?.length && l.linkState !== 'ignored' && l.linkState !== 'internal';
  const category = (l: BankTransaction) => l.parts?.length ? <SplitParts line={l} />
    : l.category ? l.category.name
    : toClassify(l) ? <Badge variant="outline" className="border-transparent bg-status-warning/15 text-status-warning-text">{t('banks.operations.to_classify')}</Badge> : '—';
  // « À relier » is most lines' state: only a line linked, internal or ignored says it, under its label.
  const state = (l: BankTransaction) => l.linkState === 'unlinked' ? null : <span className="block text-xs text-muted-foreground">{lineState(t, l)}</span>;
  // A bank label can run long (references, mandates): two lines, the whole of it on hover.
  const label = (l: BankTransaction) => (
    <span className="block min-w-0">
      <span className="line-clamp-2 font-medium break-words" title={l.label}>{l.label}<Receipts line={l} /></span>
      {l.counterparty ? <span className="block truncate text-sm text-muted-foreground">{l.counterparty}</span> : null}
      {l.note ? <span className="block text-sm italic text-muted-foreground">{l.note}</span> : null}
      {state(l)}
      {/* #416: an immediate card's purchase stands for its echo on the account, which the list leaves out. */}
      {l.debitedFrom ? <span className="block text-xs text-muted-foreground">{t('banks.operations.debited_from', { name: l.debitedFrom })}</span> : null}
    </span>
  );
  const account = (l: BankTransaction) => (
    <span className="inline-flex items-center gap-2">{owner(l)}<span className="text-sm text-muted-foreground">{l.accountName}</span></span>
  );
  return (
    <SortableTable<BankTransaction> caption={t('banks.operations.title')} rows={rows} rowKey={(l) => l.id}
      selection={{ selected, onChange: onSelect, label: (l) => t('banks.operations.select', { label: l.label }), allLabel: t('banks.operations.select_page'),
        disabled: (l) => !categorisable(l) }}
      columns={[
        { id: 'date', header: t('banks.operations.date'), className: 'w-24 text-muted-foreground', cell: (l) => <LocalDate value={l.bookedOn} style="short" /> },
        { id: 'label', header: t('banks.operations.label'), wrap: true, className: 'w-full min-w-56', cell: label },
        { id: 'category', header: t('banks.operations.filter.category'), wrap: true, className: 'min-w-36', cell: category },
        { id: 'account', header: t('banks.operations.account'), cell: account },
        { id: 'amount', header: t('banks.operations.amount'), align: 'end', cell: amount },
        { id: 'actions', header: t('banks.operations.actions_col'), align: 'end', className: 'w-10', cell: actions },
      ]}
      mobile={(l) => (
        <div className="space-y-1">
          <div className="flex items-start justify-between gap-2">{label(l)}{amount(l)}</div>
          <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">
            <span className="flex min-w-0 flex-wrap items-center gap-x-2"><LocalDate value={l.bookedOn} style="short" /> · {category(l)}</span>
            {actions(l)}
          </div>
          <div className="text-sm">{account(l)}</div>
        </div>
      )} />
  );
}
