import { keepPreviousData } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ASSETS, type AssetList, type AssetRow } from '@/api/assets';
import { present, toProblem } from '@/api/errors';
import { PERSONS } from '@/api/persons';
import type { AssetsSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { SelectField } from '@/components/form/SelectField';
import { OwnerChips } from '@/components/OwnerChips';
import { AccessBadge } from '@/components/AccessBadge';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { PageHeader } from '@/components/page/PageHeader';
import { SearchInput } from '@/components/page/SearchInput';
import { PAGE_SIZE, Pager } from '@/components/page/Pager';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { buttonVariants } from '@/components/ui/button';
import { kindText } from './format';
import '@/locales/assets';
import '@/locales/forms';

/** The other assets: a dense table, its filters, search and page in the URL and answered by the server (issue #420's grammar). */
export function AssetsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: AssetsSearch & { scope?: 'me' } = useSearch({ strict: false });
  const page = search.page ?? 1;
  const answer = $api.useQuery('get', ASSETS, {
    params: { query: { scope: search.scope === 'me' ? 'me' : 'household', kind: search.kind, owner: search.owner, archived: search.archived === 1, q: search.q, page } },
  }, { placeholderData: keepPreviousData });
  const people = $api.useQuery('get', PERSONS);
  const go = (change: Partial<AssetsSearch>) =>
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: AssetsSearch) => ({ ...prev, page: undefined, ...change })) as never });
  const rows = answer.data ? grouped(answer.data) : [];
  const total = answer.data?.total ?? 0;
  const narrowed = Boolean(search.kind || search.owner || search.q || search.archived);
  // A page past the end (the last rows archived) steps back.
  const settled = answer.data && !answer.isPlaceholderData ? answer.data : undefined;
  useEffect(() => {
    if (settled && settled.rows.length === 0 && page > 1) go({ page: Math.ceil(settled.total / PAGE_SIZE) > 1 ? Math.ceil(settled.total / PAGE_SIZE) : undefined });
  });
  const add = (variant?: 'outline') => <Link to="/other-assets/new" className={buttonVariants({ variant })}>{t('assets_list.add')}</Link>;
  const kinds = answer.data?.kinds ?? [];

  return (
    <div className="space-y-4">
      <PageHeader title={t('assets_list.title')} actions={add()} />
      <FilterRow label={t('common.filters')}>
        <FilterField label={t('assets_list.search')} labelFor className="w-full sm:w-auto">
          {(labelId, controlId) => <SearchInput id={controlId} labelledBy={labelId} value={search.q ?? ''} onSearch={(q) => go({ q: q || undefined })} />}
        </FilterField>
        <FilterField label={t('assets_list.filter_kind')} className="w-full sm:w-auto">
          {(labelId, controlId) => (
            <div className="min-w-0 flex-1 sm:w-52 sm:flex-none">
              <SelectField id={controlId} aria-labelledby={labelId} value={search.kind ?? ''} onValueChange={(kind) => go({ kind: kind || undefined })}
                options={[{ value: '', label: t('assets_list.filter_kind_all') }, ...kinds.map((k) => ({ value: k, label: kindText(t, k) }))]} />
            </div>
          )}
        </FilterField>
        <FilterField label={t('assets_list.filter_owner')} className="w-full sm:w-auto">
          {(labelId, controlId) => (
            <div className="min-w-0 flex-1 sm:w-52 sm:flex-none">
              <SelectField id={controlId} aria-labelledby={labelId} value={search.owner ?? ''} onValueChange={(owner) => go({ owner: owner || undefined })}
                options={[{ value: '', label: t('assets_list.filter_owner_all') }, ...(people.data ?? []).map((p) => ({ value: p.id, label: p.displayName }))]} />
            </div>
          )}
        </FilterField>
        <SwitchField label={t('assets_list.show_archived')} checked={search.archived === 1} onCheckedChange={(on) => go({ archived: on ? 1 : undefined })} />
      </FilterRow>
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data ? <Loading label={t('assets_list.loading')}><TableSkeleton columns={4} /></Loading>
        : total === 0 && !narrowed ? <EmptyState module="/other-assets" title={t('assets_list.empty_title')} body={t('assets_list.empty_body')} action={add('outline')} />
        : rows.length === 0 ? <p className="text-muted-foreground">{t('assets_list.no_match')}</p>
        : (
          <>
            <Table rows={rows} />
            <Pager page={page} total={total} label={t('assets_list.pages')} onPage={(n) => go({ page: n > 1 ? n : undefined })} />
          </>
        )}
    </div>
  );
}

type Row = AssetRow & { parts?: AssetRow[]; total?: AssetList['groups'][number]['value'] };

/** As the income list: the assets of the same owners fold into one row, its total the server's; a group of one stays a plain row. */
function grouped(data: AssetList): Row[] {
  const byId = new Map(data.rows.map((r) => [r.id, r]));
  return data.groups.flatMap((g) => {
    const parts = g.items.flatMap((id) => byId.get(id) ?? []);
    return parts.length > 1 ? [{ ...parts[0]!, parts, total: g.value }] : parts;
  });
}

function Table({ rows }: { rows: Row[] }) {
  const { t } = useTranslation();
  const name = (r: Row) => r.parts ? <span className="font-medium">{t('assets_list.group', { count: r.parts.length })}</span> : (
    <span className="inline-flex flex-wrap items-center gap-2">
      <Link to="/other-assets/$id" params={{ id: r.id }} className="font-medium underline-offset-4 hover:underline">{r.name}</Link>
      <AccessBadge access={r.visibility === 'private' ? 'private' : 'shared'} />
      {r.archived ? <Badge variant="secondary">{t('assets_list.archived')}</Badge> : null}
    </span>
  );
  const value = (r: Row) => r.parts ? (r.total ? <Money value={r.total} tabular /> : <span className="text-muted-foreground">{t('assets_list.dash')}</span>) : r.value ? (
    <span className="inline-flex flex-col items-end">
      <span className="inline-flex items-center gap-2">
        {r.valueEstimated ? <Badge variant="secondary">{t('assets_list.estimated')}</Badge> : r.valueFromPurchase ? <Badge variant="secondary">{t('assets_list.purchase_price')}</Badge> : null}
        <Money value={r.value} tabular />
      </span>
      {r.valueOn ? <span className="text-xs text-muted-foreground">{r.valueEstimated ? <>{t('assets_list.estimated_base')} </> : null}<LocalDate value={r.valueOn} /></span> : null}
    </span>
  ) : <span className="text-muted-foreground">{t('assets_list.no_value')}</span>;
  return (
    <SortableTable<Row> caption={t('assets_list.list')} rows={rows} rowKey={(r) => (r.parts ? `group:${r.id}` : r.id)}
      subRows={(r) => r.parts} expandLabel={() => t('assets_list.expand_group')}
      rowClassName={(r) => (r.parts ? 'bg-muted/50' : undefined)}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (r) => r.name, cell: name },
        { id: 'kind', header: t('lists.col.kind'), sort: (r) => kindText(t, r.kind), cell: (r) => (r.parts ? null : kindText(t, r.kind)) },
        { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
        { id: 'value', header: t('lists.col.value'), align: 'end', sort: (r) => amountKey((r.parts ? r.total : r.value)?.amount), cell: value },
      ]}
      mobile={(r) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2">{name(r)}<OwnerChips owners={r.owners} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm"><span className="text-muted-foreground">{r.parts ? null : kindText(t, r.kind)}</span>{value(r)}</div>
        </div>
      )} />
  );
}
