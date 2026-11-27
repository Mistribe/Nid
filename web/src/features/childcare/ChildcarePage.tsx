import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { ChildcareListSearch } from '@/app/router';
import type { RootSearch } from '@/lib/search';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { foldByOwners, type Folded } from '@/components/page/foldByOwners';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { Hero } from '@/components/page/Hero';
import { PageHeader } from '@/components/page/PageHeader';
import { SectionCard } from '@/components/page/SectionCard';
import { StatLine } from '@/components/page/StatLine';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { type ChildcareList, type ChildcareSummary, useChildcare, useChildcareList } from './api';
import { type NannySummary, useNannies } from './nannyApi';
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
  const nannies = useNannies(scope === 'me' ? 'me' : 'household', true);
  // The empty state's action repeats the header's primary: `outline`, one primary per view (design-system.md §6.1).
  const add = (variant?: 'outline') => <Link to="/childcare/new" className={buttonVariants({ variant })}>{t('childcare_list.add')}</Link>;
  const items = answer.data?.items ?? [];
  const nannyRows = nannies.data?.items ?? [];
  const addNanny = <Link to="/childcare/nannies/new" className={buttonVariants({ variant: 'outline' })}>{t('childcare_nanny.add')}</Link>;
  const current = items.filter((c) => !c.closed);
  // Without an open arrangement there is nothing to overview: the list opens first.
  const tabs = [
    ...(current.length ? [{ id: 'overview' as const, label: t('childcare_list.tab_overview'), content: <Overview rows={current} /> }] : []),
    { id: 'list' as const, label: t('childcare_list.tab_list'), content: <List rows={items} groups={answer.data?.groups ?? []} /> },
  ];
  const [tab, setTab] = useDetailTab(tabs.map((x) => x.id));
  return (
    <div className="space-y-4">
      <PageHeader title={t('childcare_list.title')} actions={<>{addNanny}{add()}</>} />
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data || nannies.isPending ? <OverviewSkeleton label={t('childcare_list.loading')} hero stats={3} />
        : items.length === 0 && nannyRows.length === 0 ? <EmptyState module="/childcare" title={t('childcare_list.empty_title')} body={t('childcare_list.empty_body')} action={add('outline')} />
        : items.length === 0 ? null
        : <DetailTabs value={tab} onChange={setTab} tabs={tabs} />}
      {nannies.error ? <ProblemMessage problem={present(toProblem(nannies.error))} /> : nannyRows.length ? <Nannies rows={nannyRows} /> : null}
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
            // #723: the dashboard's « Garde » links here: the same figure, named the same way.
            ...(detail?.monthCost ? [{
              label: t('childcare_list.month_cost'), tip: t('childcare_list.month_cost_tip'), value: <Money value={detail.monthCost} tabular />,
              hint: detail.monthCostEstimated ? t('childcare_list.month_cost_estimated') : undefined,
            }] : []),
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

type Row = Folded<ChildcareSummary, ChildcareList['groups'][number]>;

/** Every arrangement, those of the same owners folded into one row with their open ones' pay (#802) unless the switch is off. */
function List({ rows, groups }: { rows: ChildcareSummary[]; groups: ChildcareList['groups'] }) {
  const { t } = useTranslation();
  const search: RootSearch & ChildcareListSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const flat = search.flat === 1;
  const setFlat = (on: boolean) =>
    void navigate({ to: '/childcare', search: (prev: RootSearch & ChildcareListSearch) => ({ ...prev, flat: on ? (1 as const) : undefined }), replace: true, resetScroll: false });
  const name = (c: Row) => c.parts
    ? <span className="font-medium">{t('childcare_list.group', { count: c.parts.length })}</span>
    : <Link to="/childcare/$id" params={{ id: c.id }} className="font-medium hover:underline">{c.name}</Link>;
  const pay = (c: Row) => c.group ? (c.group.monthlyBasePay ? <Money value={c.group.monthlyBasePay} tabular /> : '—') : <Money value={c.monthlyBasePay} tabular />;
  return (
    <div className="space-y-4">
      <SwitchField label={t('lists.group_by_owner')} checked={!flat} onCheckedChange={(on) => setFlat(!on)} />
      <SortableTable<Row> key={flat ? 'flat' : 'grouped'} caption={t('childcare_list.list')} rows={flat ? rows : foldByOwners(rows, groups)}
        rowKey={(c) => (c.parts ? `group:${c.id}` : c.id)}
        subRows={(c) => c.parts} expandLabel={() => t('childcare_list.expand_group')}
        rowClassName={(c) => (c.parts ? 'bg-muted/50' : undefined)}
        columns={[
          { id: 'name', header: t('lists.col.name'), sort: (c) => c.name, cell: name },
          { id: 'child', header: t('childcare_list.col_child'), sort: (c) => c.childName, cell: (c) => (c.parts ? null : c.childName) },
          { id: 'carer', header: t('childcare_list.col_carer'), sort: (c) => c.carerName, cell: (c) => (c.parts ? null : c.carerName) },
          { id: 'pay', header: t('childcare_list.col_pay'), align: 'end', sort: (c) => amountKey(c.group ? c.group.monthlyBasePay?.amount : c.monthlyBasePay.amount), cell: pay },
          { id: 'start', header: t('childcare_list.col_start'), sort: (c) => c.start, cell: (c) => (c.parts ? null : <LocalDate value={c.start} style="short" />) },
          { id: 'to_prepare', header: t('childcare_list.col_to_prepare'), align: 'end', sort: (c) => c.toPrepare.length,
            cell: (c) => (c.parts ? null : c.toPrepare.length > 0 ? t('childcare_list.to_prepare', { count: c.toPrepare.length }) : '—') },
          { id: 'owner', header: t('childcare_list.col_owners'), cell: (c) => <OwnerChips owners={c.owners} /> },
        ]}
        mobile={(c) => (
          <div className="space-y-1">
            <div className="flex items-center justify-between gap-2">{name(c)}<OwnerChips owners={c.owners} /></div>
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted-foreground">
              <span>{c.parts ? null : `${c.childName} · ${c.carerName}`}</span>{pay(c)}
            </div>
            {!c.parts && c.toPrepare.length > 0 ? <p className="text-sm">{t('childcare_list.to_prepare_mobile', { count: c.toPrepare.length })}</p> : null}
          </div>
        )} />
    </div>
  );
}

/** #94: the nannies at home, each with the current year's net cost and months recorded, the server's figures. */
function Nannies({ rows }: { rows: NannySummary[] }) {
  const { t } = useTranslation();
  const name = (n: NannySummary) => <Link to="/childcare/nannies/$id" params={{ id: n.id }} className="font-medium hover:underline">{n.name}</Link>;
  return (
    <SectionCard title={t('childcare_nanny.section')}>
      <SortableTable<NannySummary> caption={t('childcare_nanny.section')} rows={rows} rowKey={(n) => n.id}
        columns={[
          { id: 'name', header: t('lists.col.name'), sort: (n) => n.name, cell: (n) => <>{name(n)}{n.closed ? <span className="text-muted-foreground"> · {t('childcare_nanny.closed_badge')}</span> : null}</> },
          { id: 'net', header: t('childcare_nanny.net_cost_year'), align: 'end', sort: (n) => amountKey(n.netCost.amount), cell: (n) => <Money value={n.netCost} tabular /> },
          { id: 'months', header: t('childcare_nanny.months_recorded'), align: 'end', sort: (n) => n.months, cell: (n) => t('childcare_nanny.months', { count: n.months }) },
          { id: 'owner', header: t('childcare_list.col_owners'), cell: (n) => <OwnerChips owners={n.owners} /> },
        ]}
        mobile={(n) => (
          <div className="flex flex-wrap items-center justify-between gap-2">{name(n)}<Money value={n.netCost} tabular /></div>
        )} />
    </SectionCard>
  );
}
