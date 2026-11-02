import { Link, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { type ChildcareSummary, useChildcareList } from './api';
import '@/locales/childcare';
import '@/locales/forms';
import { Loading, TableSkeleton } from '@/components/page/skeletons';

const TABS = ['open', 'closed'] as const;

/** Spec §5: the arrangements, open ones first, closed ones under their own tab (server order kept until a column is sorted). */
export function ChildcarePage() {
  const { t } = useTranslation();
  const { scope }: RootSearch = useSearch({ strict: false });
  const [tab, setTab] = useDetailTab(TABS);
  // The empty state's action repeats the header's primary: `outline`, one primary per view (design-system.md §6.1).
  const add = (variant?: 'outline') => <Link to="/childcare/new" className={buttonVariants({ variant })}>{t('childcare_list.add')}</Link>;
  const scoped = scope === 'me' ? 'me' : 'household';
  return (
    <div className="space-y-4">
      <PageHeader title={t('childcare_list.title')} actions={add()} />
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'open', label: t('childcare_list.tab_open'), content: <List scope={scoped} closed={false} add={add('outline')} /> },
        { id: 'closed', label: t('childcare_list.tab_closed'), content: <List scope={scoped} closed add={add('outline')} /> },
      ]} />
    </div>
  );
}

function List({ scope, closed, add }: { scope: 'me' | 'household'; closed: boolean; add: React.ReactNode }) {
  const { t } = useTranslation();
  const answer = useChildcareList(scope, closed);
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data)
    return (
      <Loading label={t('childcare_list.loading')}><TableSkeleton columns={6} /></Loading>
    );
  // `closed=true` lists the open arrangements too: this tab keeps the closed ones.
  const rows = closed ? answer.data.items.filter((c) => c.closed) : answer.data.items;
  if (rows.length === 0)
    return closed
      ? <EmptyState module="/childcare" title={t('childcare_list.empty_closed_title')} body={t('childcare_list.empty_closed_body')} />
      : <EmptyState module="/childcare" title={t('childcare_list.empty_title')} body={t('childcare_list.empty_body')} action={add} />;

  const name = (c: ChildcareSummary) => <Link to="/childcare/$id" params={{ id: c.id }} className="font-medium hover:underline">{c.name}</Link>;
  const pay = (c: ChildcareSummary) => <Money value={c.monthlyBasePay} tabular />;
  return (
    <SortableTable<ChildcareSummary> caption={t('childcare_list.list')} rows={rows} rowKey={(c) => c.id}
      columns={[
        { id: 'name', header: t('lists.col.name'), sort: (c) => c.name, cell: name },
        { id: 'child', header: t('childcare_list.col_child'), sort: (c) => c.childName, cell: (c) => c.childName },
        { id: 'carer', header: t('childcare_list.col_carer'), sort: (c) => c.carerName, cell: (c) => c.carerName },
        { id: 'pay', header: t('childcare_list.col_pay'), align: 'end', sort: (c) => amountKey(c.monthlyBasePay.amount), cell: pay },
        { id: 'start', header: t('childcare_list.col_start'), sort: (c) => c.start, cell: (c) => <LocalDate value={c.start} style="short" /> },
        { id: 'owner', header: t('childcare_list.col_owners'), cell: (c) => <OwnerChips owners={c.owners} /> },
      ]}
      mobile={(c) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2">{name(c)}<OwnerChips owners={c.owners} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted-foreground">
            <span>{c.childName} · {c.carerName}</span>{pay(c)}
          </div>
        </div>
      )} />
  );
}
