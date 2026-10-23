import { useQueryClient } from '@tanstack/react-query';
import { Link, useSearch } from '@tanstack/react-router';
import { useState } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { PageHeader } from '@/components/page/PageHeader';
import { RowActions } from '@/components/page/RowActions';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { percent } from '@/features/dashboard/format';
import { presentConflict } from '../investments/entries/entries';
import { SCENARIO, SCENARIOS, type ScenarioListItem } from './api';

/** Scenarios list (spec §7): a dense table, a row's `⋯` duplicates or deletes, creation is its own page. */
export function ScenariosPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const answer = $api.useQuery('get', SCENARIOS, { params: { query: { scope: search.scope === 'me' ? 'me' : 'household' } } });
  const add = <Link to="/scenarios/new" className={buttonVariants()}>{t('scenarios.add')}</Link>;

  return (
    <div className="space-y-4">
      <PageHeader title={t('scenarios.title')} actions={add} />
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

const usageText = (t: TFunction, use: string) => (use === 'rental' ? t('scenarios.use.rental') : t('scenarios.use.primary'));

function List({ items }: { items: ScenarioListItem[] }) {
  const { t } = useTranslation();
  const ratio = (r: ScenarioListItem) => r.debtRatioAfter == null ? t('scenarios.dash') : t('scenarios.percent', { value: percent(r.debtRatioAfter) });
  return (
    <SortableTable<ScenarioListItem> caption={t('scenarios.list')} rows={items} rowKey={(r) => r.id}
      rowLink={(r) => ({ to: '/scenarios/$id', params: { id: r.id } })}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (r) => r.name, cell: (r) => <span className="font-medium">{r.name}</span> },
        { id: 'use', header: t('scenarios.col.use'), sort: (r) => usageText(t, r.use), cell: (r) => usageText(t, r.use) },
        { id: 'price', header: t('scenarios.col.price'), align: 'end', sort: (r) => amountKey(r.price.amount), cell: (r) => <Money value={r.price} tabular /> },
        { id: 'payment', header: t('scenarios.col.payment'), align: 'end', sort: (r) => amountKey(r.newPayment.amount), cell: (r) => <Money value={r.newPayment} tabular /> },
        { id: 'ratio', header: t('scenarios.col.ratio'), align: 'end', sort: (r) => r.debtRatioAfter ?? Number.NaN, cell: (r) => <span className="tabular-nums">{ratio(r)}</span> },
        { id: 'gap', header: t('scenarios.col.gap'), align: 'end', sort: (r) => amountKey(r.gap.amount), cell: (r) => <Money value={r.gap} tabular /> },
        { id: 'owner', header: t('lists.col.owner'), cell: (r) => <OwnerChips owners={r.owners} /> },
        { id: 'actions', header: t('scenarios.col.actions'), align: 'end', cell: (r) => <Actions item={r} /> },
      ]}
      mobile={(r) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2"><span className="font-medium">{r.name}</span><OwnerChips owners={r.owners} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span>{usageText(t, r.use)}</span><Money value={r.price} tabular /><Money value={r.newPayment} tabular />
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
  const remove = $api.useMutation('delete', SCENARIO, { onSuccess: () => void invalidate() });
  // The list item carries no document: read the scenario, then create a copy of it.
  const duplicate = $api.useMutation('post', SCENARIOS, { onSuccess: () => void invalidate() });
  const [copyError, setCopyError] = useState<unknown>(null);
  const copy = async () => {
    setCopyError(null);
    duplicate.reset();
    try {
      const { data, error } = await fetchClient.GET(SCENARIO, { params: { path: { id: item.id } } });
      if (!data) { setCopyError(error ?? new Error('copy failed')); return; }
      duplicate.mutate({ body: { name: t('scenarios.copy_suffix', { name: item.name }), document: data.document, visibility: data.visibility, scope: data.scope, owners: item.owners } });
    } catch (e) { setCopyError(e); }
  };
  const failure = copyError ?? duplicate.error;
  return (
    <>
      <RowActions menuId={`scenario-menu-${item.id}`} menuLabel={t('scenarios.row_menu', { name: item.name })}
      items={[{ label: t('scenarios.duplicate'), onSelect: () => void copy() }]}
      confirmation={t('scenarios.delete_confirm')} remove={remove} onDeleteStarted={() => undefined} present={presentConflict}
      invalidate={(client) => invalidate(client)}
      onConfirm={() => remove.mutate({ params: { path: { id: item.id } } })} />
      {failure ? <ProblemMessage problem={present(toProblem(failure))} /> : null}
    </>
  );
}
