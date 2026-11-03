import { useQueryClient } from '@tanstack/react-query';
import { Link, useSearch } from '@tanstack/react-router';
import { useState } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { Money, type MoneyValue } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { PageHeader } from '@/components/page/PageHeader';
import { RowActions } from '@/components/page/RowActions';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { percent } from '@/features/dashboard/format';
import { presentConflict } from '../investments/entries/entries';
import { PROJECTION_SCENARIO, PROJECTION_SCENARIOS, SCENARIO, SCENARIOS, type ScenarioListItem } from './api';
import '@/locales/forms';
import '@/locales/scenarios';

/**
 * Scenarios list (purchase spec §7, projection spec §8): a dense table of both kinds, a row's `⋯` duplicates or deletes, creation is
 * its own page, for the kind « Nouveau scénario » asks for.
 */
export function ScenariosPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const answer = $api.useQuery('get', SCENARIOS, { params: { query: { scope: search.scope === 'me' ? 'me' : 'household' } } });
  const add = <AddMenu />;

  return (
    <div className="space-y-4">
      <PageHeader title={t('scenarios.title')} actions={(
        <>
          <Button asChild variant="outline">
            <Link to="/scenarios/compare" search={search.scope === 'me' ? { scope: 'me' } : {}}>{t('scenarios.compare.open')}</Link>
          </Button>
          {add}
        </>
      )} />
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data ? (
          <ul aria-busy="true" aria-label={t('scenarios.loading')} className="space-y-2">
            {[0, 1, 2].map((i) => <li key={i} className="h-16 animate-pulse rounded-md border bg-muted" />)}
          </ul>
        ) : answer.data.items.length === 0
          ? <EmptyState title={t('scenarios.empty_title')} body={t('scenarios.empty_body')} action={add} />
          : <List items={answer.data.items} />}
    </div>
  );
}

/** « Nouveau scénario » asks for the kind: a purchase keeps its own page, a projection is `?kind=projection`. */
function AddMenu() {
  const { t } = useTranslation();
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild><Button>{t('scenarios.add')}</Button></DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem asChild><Link to="/scenarios/new">{t('scenarios.add_menu.property_purchase')}</Link></DropdownMenuItem>
        <DropdownMenuItem asChild><Link to="/scenarios/new" search={{ kind: 'projection' }}>{t('scenarios.add_menu.projection')}</Link></DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

const isProjection = (r: ScenarioListItem) => r.kind === 'projection';
const kindText = (t: TFunction, r: ScenarioListItem) => (isProjection(r) ? t('scenarios.kind.projection') : t('scenarios.kind.property_purchase'));

// A projection scenario has no use, price nor payment: dashes; its gap is the server's nominal gap at the horizon.
const usageText = (t: TFunction, use: string | null | undefined) =>
  use == null ? t('scenarios.dash') : use === 'rental' ? t('scenarios.use.rental') : t('scenarios.use.primary');

const MaybeMoney = ({ value }: { value: MoneyValue | null | undefined }) => {
  const { t } = useTranslation();
  return value ? <Money value={value} tabular /> : <span>{t('scenarios.dash')}</span>;
};

function List({ items }: { items: ScenarioListItem[] }) {
  const { t } = useTranslation();
  const ratio = (r: ScenarioListItem) => r.debtRatioAfter == null ? t('scenarios.dash') : t('scenarios.percent', { value: percent(r.debtRatioAfter) });
  return (
    <SortableTable<ScenarioListItem> caption={t('scenarios.list')} rows={items} rowKey={(r) => r.id}
      rowLink={(r) => (isProjection(r) ? { to: '/scenarios/projection/$id', params: { id: r.id } } : { to: '/scenarios/$id', params: { id: r.id } })}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (r) => r.name, cell: (r) => <span className="font-medium">{r.name}</span> },
        { id: 'kind', header: t('scenarios.col.kind'), sort: (r) => kindText(t, r), cell: (r) => kindText(t, r) },
        { id: 'use', header: t('scenarios.col.use'), sort: (r) => usageText(t, r.use), cell: (r) => usageText(t, r.use) },
        { id: 'price', header: t('scenarios.col.price'), align: 'end', sort: (r) => amountKey(r.price?.amount), cell: (r) => <MaybeMoney value={r.price} /> },
        { id: 'payment', header: t('scenarios.col.payment'), align: 'end', sort: (r) => amountKey(r.newPayment?.amount), cell: (r) => <MaybeMoney value={r.newPayment} /> },
        { id: 'ratio', header: t('scenarios.col.ratio'), align: 'end', sort: (r) => r.debtRatioAfter ?? Number.NaN, cell: (r) => <span className="tabular-nums">{ratio(r)}</span> },
        { id: 'gap', header: t('scenarios.col.gap'), align: 'end', sort: (r) => amountKey(r.gap?.amount), cell: (r) => <MaybeMoney value={r.gap} /> },
        { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
        { id: 'actions', header: t('scenarios.col.actions'), align: 'end', cell: (r) => <Actions item={r} /> },
      ]}
      mobile={(r) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2"><span className="font-medium">{r.name}</span><OwnerChips owners={r.owners} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span>{kindText(t, r)}</span>
            {isProjection(r) ? <MaybeMoney value={r.gap} /> : <><span>{usageText(t, r.use)}</span><MaybeMoney value={r.price} /><MaybeMoney value={r.newPayment} /></>}
          </div>
        </div>
      )} />
  );
}

function Actions({ item }: { item: ScenarioListItem }) {
  const { t } = useTranslation();
  const { $api, fetchClient } = useApi();
  const queryClient = useQueryClient();
  const invalidate = (client = queryClient) => client.invalidateQueries({ queryKey: ['get', SCENARIOS] });
  // The deletion is shared by both kinds; reading and creating are each kind's own routes.
  const remove = $api.useMutation('delete', SCENARIO, { onSuccess: () => void invalidate() });
  // The list item carries no document: read the scenario, then create a copy of it.
  const duplicate = $api.useMutation('post', SCENARIOS, { onSuccess: () => void invalidate() });
  const duplicateProjection = $api.useMutation('post', PROJECTION_SCENARIOS, { onSuccess: () => void invalidate() });
  const [copyError, setCopyError] = useState<unknown>(null);
  const copy = async () => {
    setCopyError(null);
    duplicate.reset();
    duplicateProjection.reset();
    const name = t('scenarios.copy_suffix', { name: item.name });
    try {
      if (isProjection(item)) {
        const { data, error } = await fetchClient.GET(PROJECTION_SCENARIO, { params: { path: { id: item.id } } });
        if (!data) { setCopyError(error ?? new Error('copy failed')); return; }
        duplicateProjection.mutate({ body: { name, document: data.result.document, visibility: data.visibility, scope: data.scope, owners: item.owners } });
        return;
      }
      const { data, error } = await fetchClient.GET(SCENARIO, { params: { path: { id: item.id } } });
      if (!data) { setCopyError(error ?? new Error('copy failed')); return; }
      duplicate.mutate({ body: { name, document: data.document, visibility: data.visibility, scope: data.scope, owners: item.owners } });
    } catch (e) { setCopyError(e); }
  };
  const failure = copyError ?? duplicate.error ?? duplicateProjection.error;
  return (
    <>
      <RowActions menuId={`scenario-menu-${item.id}`} menuLabel={t('scenarios.row_menu', { name: item.name })}
      items={[{ label: t('scenarios.duplicate'), onSelect: () => void copy() }]}
      confirmation={t('scenarios.delete_confirm')} remove={remove} present={presentConflict}
      invalidate={(client) => invalidate(client)}
      onConfirm={() => remove.mutate({ params: { path: { id: item.id } } })} />
      {failure ? <ProblemMessage problem={present(toProblem(failure))} /> : null}
    </>
  );
}
