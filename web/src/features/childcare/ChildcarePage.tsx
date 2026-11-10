import { Link, Navigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { PageHeader } from '@/components/page/PageHeader';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { type ChildcareSummary, useChildcareList } from './api';
import '@/locales/childcare';
import '@/locales/forms';
import { Loading, TableSkeleton } from '@/components/page/skeletons';

/** Spec §5: the current arrangement opens at once; without one, the closed arrangements (server order kept until a column is sorted). */
export function ChildcarePage() {
  const { t } = useTranslation();
  const { scope }: RootSearch = useSearch({ strict: false });
  const answer = useChildcareList(scope === 'me' ? 'me' : 'household', true);
  // The empty state's action repeats the header's primary: `outline`, one primary per view (design-system.md §6.1).
  const add = (variant?: 'outline') => <Link to="/childcare/new" className={buttonVariants({ variant })}>{t('childcare_list.add')}</Link>;
  const current = answer.data?.items.find((c) => !c.closed);
  if (current) return <Navigate to="/childcare/$id" params={{ id: current.id }} replace />;
  return (
    <div className="space-y-4">
      <PageHeader title={t('childcare_list.title')} actions={add()} />
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data ? <Loading label={t('childcare_list.loading')}><TableSkeleton columns={7} /></Loading>
        : answer.data.items.length === 0 ? <EmptyState module="/childcare" title={t('childcare_list.empty_title')} body={t('childcare_list.empty_body')} action={add('outline')} />
        : <List rows={answer.data.items} />}
    </div>
  );
}

function List({ rows }: { rows: ChildcareSummary[] }) {
  const { t } = useTranslation();
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
        { id: 'to_prepare', header: t('childcare_list.col_to_prepare'), align: 'end', sort: (c) => c.toPrepare.length,
          cell: (c) => (c.toPrepare.length > 0 ? t('childcare_list.to_prepare', { count: c.toPrepare.length }) : '—') },
        { id: 'owner', header: t('childcare_list.col_owners'), cell: (c) => <OwnerChips owners={c.owners} /> },
      ]}
      mobile={(c) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2">{name(c)}<OwnerChips owners={c.owners} /></div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted-foreground">
            <span>{c.childName} · {c.carerName}</span>{pay(c)}
          </div>
          {c.toPrepare.length > 0 ? <p className="text-sm">{t('childcare_list.to_prepare_mobile', { count: c.toPrepare.length })}</p> : null}
        </div>
      )} />
  );
}
