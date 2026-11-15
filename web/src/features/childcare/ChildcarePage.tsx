import { Link, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { Hero } from '@/components/page/Hero';
import { PageHeader } from '@/components/page/PageHeader';
import { SectionCard } from '@/components/page/SectionCard';
import { StatLine } from '@/components/page/StatLine';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { type ChildcareSummary, useChildcare, useChildcareList } from './api';
import { monthTitle } from './statementText';
import '@/locales/childcare';
import '@/locales/forms';
import { OverviewSkeleton } from '@/components/page/skeletons';

/**
 * Spec §5, page grammar of the other modules: the open arrangements' overview first (a hero per arrangement, its months to
 * prepare), then the list of every arrangement, the closed ones included (server order kept until a column is sorted).
 */
export function ChildcarePage() {
  const { t } = useTranslation();
  const { scope }: RootSearch = useSearch({ strict: false });
  const answer = useChildcareList(scope === 'me' ? 'me' : 'household', true);
  // The empty state's action repeats the header's primary: `outline`, one primary per view (design-system.md §6.1).
  const add = (variant?: 'outline') => <Link to="/childcare/new" className={buttonVariants({ variant })}>{t('childcare_list.add')}</Link>;
  const items = answer.data?.items ?? [];
  const current = items.filter((c) => !c.closed);
  // Without an open arrangement there is nothing to overview: the list opens first.
  const tabs = [
    ...(current.length ? [{ id: 'overview' as const, label: t('childcare_list.tab_overview'), content: <Overview rows={current} /> }] : []),
    { id: 'list' as const, label: t('childcare_list.tab_list'), content: <List rows={items} /> },
  ];
  const [tab, setTab] = useDetailTab(tabs.map((x) => x.id));
  return (
    <div className="space-y-4">
      <PageHeader title={t('childcare_list.title')} actions={add()} />
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data ? <OverviewSkeleton label={t('childcare_list.loading')} hero stats={3} />
        : items.length === 0 ? <EmptyState module="/childcare" title={t('childcare_list.empty_title')} body={t('childcare_list.empty_body')} action={add('outline')} />
        : <DetailTabs value={tab} onChange={setTab} tabs={tabs} />}
    </div>
  );
}

function Overview({ rows }: { rows: ChildcareSummary[] }) {
  return <div className="space-y-4">{rows.map((c) => <Current key={c.id} row={c} />)}</div>;
}

/** An open arrangement: its monthly pay as the hero, the CMG, the leave and the months to prepare beside it, every figure the server's. */
function Current({ row }: { row: ChildcareSummary }) {
  const { t } = useTranslation();
  const detail = useChildcare(row.id).data;
  const dash = t('childcare_detail.dash');
  return (
    <>
      <Hero label={t('childcare_list.hero_label', { name: row.name })} figure={<Money value={row.monthlyBasePay} tabular />}
        note={(
          <>
            {t('childcare_detail.meta', { child: row.childName, carer: row.carerName })}
            {' · '}
            <Link to="/childcare/$id" params={{ id: row.id }} className="underline underline-offset-2">{t('childcare_list.open')}</Link>
          </>
        )}
        aside={(
          <StatLine items={[
            { label: t('childcare_detail.cmg_estimate'), tip: t('childcare_detail.cmg_tip'),
              value: detail?.cmgEstimate ? <Money value={detail.cmgEstimate} tabular /> : dash,
              hint: detail && !detail.cmgEstimate ? t('childcare_detail.cmg_estimate_none') : undefined },
            { label: t('childcare_detail.leave'), value: detail ? t('childcare_detail.leave_days', { count: detail.leaveDaysAccrued }) : dash },
            { label: t('childcare_list.col_to_prepare'),
              value: row.toPrepare.length ? t('childcare_list.to_prepare', { count: row.toPrepare.length }) : t('childcare_list.to_prepare_none') },
          ]} />
        )} />
      {row.toPrepare.length ? (
        <SectionCard title={t('childcare_list.to_prepare_title', { name: row.name })}>
          <GroupedList>
            {row.toPrepare.map((month) => (
              <GroupedRow key={month} link={{ to: '/childcare/$id/statements/$month', params: { id: row.id, month } }}
                title={monthTitle(month)} detail={t('childcare_list.to_prepare_row')} />
            ))}
          </GroupedList>
        </SectionCard>
      ) : null}
    </>
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
