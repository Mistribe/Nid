import { Link, useParams, useSearch } from '@tanstack/react-router';
import { CheckIcon, ChevronDownIcon } from 'lucide-react';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { PageTrail } from '@/components/page/trail';
import { SeriesLink } from '@/components/page/SeriesLink';
import { Section } from '@/components/page/Section';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ErrorPage } from '@/components/ErrorPage';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Segmented } from '@/components/form/Segmented';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useAnchoredDocuments } from '@/features/documents/api';
import { ItemDocuments } from '@/features/documents/ItemDocuments';
import type { RootSearch } from '@/lib/search';
import { useChildcare, useChildcareList, useOverrideWeek, useReopenChildcare, useStatements, type ChildcareDetail, type ChildcareTerms, type StatementRow } from './api';
import { EditChildcareDialog, EndDialog, GapsDialog, ReviseDialog } from './ChildcareDialogs';
import { dayName } from './childcareSchema';
import { stateLabel } from './statementText';
import { YearSummary } from './YearSummary';
import { reasonLabel } from './statementText';
import '@/locales/childcare';
import '@/locales/forms';
import '@/locales/deletion';
import { PageSkeleton } from '@/components/page/skeletons';

const DeleteItemFlow = lazy(() => import('@/components/DeleteItemFlow'));

type Open = 'edit' | 'revise' | 'gaps' | 'end' | 'delete' | null;

/** Hours as the server writes them (`38.00`), shown the French way (`38`, `37,5`): text only, nothing computed. */
export const hoursText = (hours: string) => hours.replace(/\.?0+$/, '').replace('.', ',');

/** Spec §5: an arrangement — its figures inline, then its statements, its template, its weeks without care and its revisions. */
export function ChildcareDetailPage() {
  const { t } = useTranslation();
  const { id } = useParams({ from: '/childcare/$id' });
  const answer = useChildcare(id);
  const [open, setOpen] = useState<Open>(null);
  const reopen = useReopenChildcare();
  const crumbs = [{ label: t('childcare_list.title'), to: '/childcare' as const }];
  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <><PageTrail crumbs={crumbs} /><PageSkeleton label={t('childcare_detail.loading')} actions={2} stats={4} tabs={4} /></>;

  const detail = answer.data;
  const { summary } = detail;
  const terms = detail.terms.find((r) => r.from === summary.inForceFrom)!;
  const show = (o: Open) => () => setOpen(o);
  const more = <SharingMenu item={id} onAnnounce={toast.success} shared={summary.visibility === 'shared'} label={t('page.more_actions')}
    items={summary.closed ? [] : [{ label: t('childcare_detail.end'), onSelect: show('end') }]}
    trailing={[{ label: t('deletion.menu'), destructive: true, onSelect: show('delete') }]} />;
  const actions = !summary.canWrite ? null : summary.closed
    ? <><Button type="button" variant="outline" disabled={reopen.isPending} onClick={() => reopen.mutate({ params: { path: { id } } })}>{t('childcare_detail.reopen')}</Button>{more}</>
    : <>
      <Button type="button" variant="outline" onClick={show('edit')}>{t('childcare_detail.edit')}</Button>
      <Button type="button" variant="outline" onClick={show('revise')}>{t('childcare_detail.revise')}</Button>
      {more}
    </>;
  const dialog = { detail, onClose: () => setOpen(null), onDone: (message: string) => { setOpen(null); toast.success(message); } };

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} crumbLabel={summary.name} title={<Contracts id={id} name={summary.name} />} actions={actions}
        meta={(
          <>
            <span className="text-sm text-muted-foreground">{t('childcare_detail.meta', { child: summary.childName, carer: summary.carerName })}</span>
            <AccessBadge access={summary.visibility === 'private' ? 'private' : 'shared'} />
            {summary.closed ? <Badge variant="secondary">{t('childcare_detail.closed_badge')}</Badge> : null}
            <OwnerChips owners={summary.owners} />
          </>
        )}>
        <section aria-label={t('childcare_detail.figures')}>
          <StatLine items={[
            { label: t('childcare_list.col_pay'), value: <Money value={summary.monthlyBasePay} tabular /> },
            { label: t('childcare_detail.weekly_hours'), value: t('childcare_detail.hours', { value: hoursText(summary.weeklyHours) }) },
            { label: t('childcare_form.weeks'), value: String(terms.programmedWeeks) },
            { label: t('childcare_detail.monthly_hours'), value: t('childcare_detail.hours', { value: hoursText(terms.monthlyHours) }) },
            { label: t('childcare_detail.normal_hours'), value: t('childcare_detail.hours', { value: String(terms.normalHours) }) },
            { label: t('childcare_detail.leave'), value: t('childcare_detail.leave_days', { count: detail.leaveDaysAccrued }) },
            // Decision 15: the quota only limits sick-child days the contract does not pay.
            ...(terms.unpaidAbsences.includes('child_sick')
              ? [{
                label: t('childcare_detail.child_sick'), tip: t('childcare_detail.child_sick_tip'),
                value: t('childcare_detail.child_sick_days', { used: detail.childSickDaysUsed, count: detail.childSickQuota }),
                hint: t('childcare_detail.child_sick_hint'),
              }] : []),
            // #385: no estimate (« — ») until a validated month carries a CMG; the server sends none rather than 0.
            detail.cmgEstimate
              ? { label: t('childcare_detail.cmg_estimate'), tip: t('childcare_detail.cmg_tip'), value: <Money value={detail.cmgEstimate} tabular /> }
              : { label: t('childcare_detail.cmg_estimate'), tip: t('childcare_detail.cmg_tip'), value: t('childcare_detail.dash'), hint: t('childcare_detail.cmg_estimate_none') },
            ...(detail.gapWeeksToPlace > 0
              ? [{ label: t('childcare_form.step_gaps'), value: t('childcare_detail.weeks', { count: detail.gapWeeksToPlace }), hint: t('childcare_detail.to_place') }] : []),
          ]} />
        </section>
      </PageHeader>
      {summary.canWrite ? null : <p>{t('childcare_detail.read_only')}</p>}
      {reopen.error ? <ProblemMessage problem={present(toProblem(reopen.error))} /> : null}
      {open === 'edit' ? <EditChildcareDialog {...dialog} /> : null}
      {open === 'revise' ? <ReviseDialog {...dialog} /> : null}
      {open === 'gaps' ? <GapsDialog {...dialog} /> : null}
      {open === 'end' ? <EndDialog {...dialog} /> : null}
      {summary.canWrite && open === 'delete'
        ? <Suspense fallback={null}><DeleteItemFlow kind="childcare.delete_childcare" id={id} closed={summary.closed} onClose={() => setOpen(null)} /></Suspense> : null}
      <Tabs detail={detail} terms={terms} onGaps={summary.canWrite && !summary.closed ? show('gaps') : null} />
    </article>
  );
}

/** The title opens the other arrangements, the closed ones included, and the creation of a new one. */
function Contracts({ id, name }: { id: string; name: string }) {
  const { t } = useTranslation();
  const { scope }: RootSearch = useSearch({ strict: false });
  const list = useChildcareList(scope === 'me' ? 'me' : 'household', true);
  return (
    <DropdownMenu>
      <DropdownMenuTrigger aria-label={t('childcare_detail.other_contracts')}
        className="inline-flex items-center gap-1 rounded-md text-left outline-none hover:text-muted-foreground focus-visible:ring-2 focus-visible:ring-ring">
        {name}<ChevronDownIcon aria-hidden="true" className="size-5 shrink-0" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="min-w-64">
        <DropdownMenuLabel>{t('childcare_detail.contracts')}</DropdownMenuLabel>
        {(list.data?.items ?? []).map((c) => (
          <DropdownMenuItem key={c.id} asChild>
            <Link to="/childcare/$id" params={{ id: c.id }} aria-current={c.id === id ? 'page' : undefined}>
              <span className="flex min-w-0 flex-1 flex-col">
                <span className="truncate">{c.name}</span>
                <span className="text-xs text-muted-foreground">
                  <LocalDate value={c.start} style="short" /> → {c.end ? <LocalDate value={c.end} style="short" /> : t('childcare_detail.current')}
                </span>
              </span>
              {c.id === id ? <CheckIcon aria-hidden="true" /> : null}
            </Link>
          </DropdownMenuItem>
        ))}
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild><Link to="/childcare/new">{t('childcare_list.add')}</Link></DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function Tabs({ detail, terms, onGaps }: { detail: ChildcareDetail; terms: ChildcareTerms; onGaps: (() => void) | null }) {
  const { t } = useTranslation();
  const tabs = [
    { id: 'statements', label: t('childcare_detail.tab_statements'), content: <Statements id={detail.summary.id} /> },
    { id: 'summary', label: t('childcare_detail.tab_summary'), content: <YearSummary id={detail.summary.id} /> },
    { id: 'planning', label: t('childcare_detail.tab_planning'), content: <Planning detail={detail} terms={terms} /> },
    { id: 'gaps', label: t('childcare_form.step_gaps'), content: <Gaps detail={detail} onEdit={onGaps} /> },
    { id: 'revisions', label: t('childcare_detail.tab_revisions'), content: <Revisions detail={detail} /> },
    { id: 'documents', label: t('childcare_detail.tab_documents'), content: <ItemDocuments itemId={detail.summary.id} canWrite={detail.summary.canWrite} defaultRole="payslip" /> },
  ];
  const [tab, setTab] = useDetailTab(tabs.map((x) => x.id));
  return <DetailTabs value={tab} onChange={setTab} tabs={tabs} />;
}

function Planning({ detail, terms }: { detail: ChildcareDetail; terms: ChildcareTerms }) {
  const { t } = useTranslation();
  const several = terms.weekTypes.length > 1;
  const mealText = (price: string | null | undefined, provider: string) => (provider === 'employer' ? t('childcare_form.provider_employer') : price ? price.replace('.', ',') : t('childcare_detail.dash'));
  return (
    <div className="space-y-4">
      <StatLine items={[
        { label: t('childcare_form.net'), value: terms.netHourlyRate.replace('.', ',') },
        { label: t('childcare_form.upkeep'), value: terms.upkeepPer9h.replace('.', ',') },
        { label: t('childcare_form.lunch_price'), value: mealText(terms.lunchPrice, terms.lunchProvider) },
        { label: t('childcare_form.snack_price'), value: mealText(terms.snackPrice, terms.snackProvider) },
        { label: t('childcare_form.overtime'), value: terms.overtimePct.replace('.', ',') },
        { label: t('childcare_form.unpaid_absences'), value: terms.unpaidAbsences.map((r) => reasonLabel(t, r)).join(', ') || t('childcare_detail.dash') },
        { label: t('childcare_detail.since'), value: <LocalDate value={terms.from} style="short" /> },
      ]} />
      {terms.weekTypes.map((w) => (
        <div key={w.name} className="space-y-1">
          {several ? <h3 className="text-sm font-medium">{t('childcare_detail.week_type', { name: w.name, hours: hoursText(w.weeklyHours), count: w.weeks })}</h3> : null}
          <ul aria-label={several ? w.name : t('childcare_detail.template')} className="space-y-1">
            {w.schedule.map((s) => (
              <li key={s.day}>{t('childcare_detail.slot', { day: dayName(t, s.day), arrival: s.arrival, departure: s.departure })}{s.lunch ? t('childcare_detail.with_lunch') : ''}{s.snack ? t('childcare_detail.with_snack') : ''}</li>
            ))}
          </ul>
        </div>
      ))}
      {several ? (
        <>
          <StatLine items={detail.allocation.map((a) => ({ label: a.name, value: `${a.allocated} / ${a.contract}` }))} />
          <ContractWeeks detail={detail} names={terms.weekTypes.map((w) => w.name)} />
        </>
      ) : null}
    </div>
  );
}

/** The contract year's weeks: the rotation gives each its type, a switch overrides it. */
function ContractWeeks({ detail, names }: { detail: ChildcareDetail; names: string[] }) {
  const { t } = useTranslation();
  const override = useOverrideWeek();
  const editable = detail.summary.canWrite && !detail.summary.closed;
  const options = names.map((name, i) => ({ value: String(i), label: name }));
  return (
    <div className="space-y-2">
      {override.error ? <ProblemMessage problem={present(toProblem(override.error))} /> : null}
      <Table>
        <caption className="sr-only">{t('childcare_detail.contract_weeks')}</caption>
        <TableHeader>
          <TableRow><TableHead>{t('childcare_detail.col_week')}</TableHead><TableHead>{t('childcare_detail.col_week_type')}</TableHead></TableRow>
        </TableHeader>
        <TableBody>
          {detail.weeks.map((w) => (
            <TableRow key={w.monday}>
              <TableCell><LocalDate value={w.monday} style="short" /></TableCell>
              <TableCell className="space-x-2">
                {editable
                  ? <Segmented disabled={override.isPending} aria-label={t('childcare_detail.week_of', { date: formatLocalDate(w.monday, 'short') })} value={String(w.weekType)} options={options}
                    onValueChange={(v) => override.mutate({ params: { path: { id: detail.summary.id, monday: w.monday } }, body: { weekType: Number(v) } })} />
                  : <span>{names[w.weekType]}</span>}
                {w.overridden ? <Badge variant="outline" className="text-status-warning-text">{t('childcare_detail.overridden')}</Badge> : null}
                {w.gap ? <span className="text-sm text-muted-foreground">{t('childcare_detail.no_care')}</span> : null}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

function Gaps({ detail, onEdit }: { detail: ChildcareDetail; onEdit: (() => void) | null }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-3">
      {detail.gaps.length === 0 ? <p className="text-sm text-muted-foreground">{t('childcare_detail.no_gaps')}</p> : (
        <ul aria-label={t('childcare_form.step_gaps')} className="space-y-1">
          {detail.gaps.map((g) => (
            <li key={g.monday}>
              <LocalDate value={g.monday} style="short" /> → <LocalDate value={g.sunday} style="short" /> · {t('childcare_detail.weeks', { count: g.weeks })}
            </li>
          ))}
        </ul>
      )}
      {onEdit ? <Button type="button" variant="outline" onClick={onEdit}>{t('childcare_detail.edit_gaps')}</Button> : null}
    </div>
  );
}

function Revisions({ detail }: { detail: ChildcareDetail }) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState<ChildcareTerms | null>(null);
  const editable = detail.summary.canWrite && !detail.summary.closed;
  // Only the last terms are open-ended: in force, or a revision that has not started yet.
  const end = (r: ChildcareTerms) => r.to ? <LocalDate value={r.to} style="short" /> : t(r.from > detail.summary.inForceFrom ? 'childcare_detail.upcoming' : 'childcare_detail.current');
  // A past revision stays correctable in full, the current terms too.
  const menu = (r: ChildcareTerms) => editable
    ? <RowMenu label={t('childcare_detail.revision_menu', { from: formatLocalDate(r.from, 'short') })} items={[{ label: t('childcare_detail.revision_edit'), onSelect: () => setEditing(r) }]} />
    : null;
  const absences = (r: ChildcareTerms) => r.unpaidAbsences.map((a) => reasonLabel(t, a)).join(', ') || t('childcare_detail.dash');
  return (
    <>
      <SortableTable<ChildcareTerms> caption={t('childcare_detail.tab_revisions')} rows={[...detail.terms].reverse()} rowKey={(r) => r.from}
        columns={[
          { id: 'from', header: t('childcare_detail.col_from'), sort: (r) => r.from, cell: (r) => <LocalDate value={r.from} style="short" /> },
          { id: 'to', header: t('childcare_detail.col_to'), sort: (r) => r.to ?? '', cell: end },
          { id: 'rate', header: t('childcare_form.net'), align: 'end', cell: (r) => r.netHourlyRate.replace('.', ',') },
          { id: 'pay', header: t('childcare_list.col_pay'), align: 'end', cell: (r) => <Money value={r.monthlyBasePay} tabular /> },
          { id: 'unpaid', header: t('childcare_form.unpaid_absences'), wrap: true, cell: absences },
          ...(editable ? [{ id: 'menu', header: t('childcare_detail.col_actions'), align: 'end' as const, cell: menu }] : []),
        ]}
        mobile={(r) => (
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span><LocalDate value={r.from} style="short" /> → {end(r)}</span>
            <span className="flex items-center gap-2"><Money value={r.monthlyBasePay} tabular />{menu(r)}</span>
            <span className="w-full text-muted-foreground">{absences(r)}</span>
          </div>
        )} />
      {editing ? <EditChildcareDialog detail={detail} revision={editing.from} onClose={() => setEditing(null)}
        onDone={(message) => { setEditing(null); toast.success(message); }} /> : null}
    </>
  );
}

function Statements({ id }: { id: string }) {
  const { t } = useTranslation();
  const answer = useStatements(id);
  const documents = useAnchoredDocuments(id);
  // Decision 23: a month has its payslip when a document of the arrangement, role payslip, is dated in it (compared as text).
  const withPayslip = new Set((documents.data?.items ?? [])
    .filter((d) => d.links.some((l) => l.targetKind === 'core.item' && l.targetId === id && l.role === 'payslip'))
    .flatMap((d) => (d.documentDate ? [d.documentDate.slice(0, 7)] : [])));
  const missing = (r: StatementRow) => r.state === 'validated' && documents.data !== undefined && !withPayslip.has(r.month);
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <div aria-busy="true" className="h-24 shimmer rounded-md border bg-muted" />;
  // Server order (latest first), one grid per calendar year.
  const years = new Map<string, StatementRow[]>();
  for (const r of answer.data.months) years.set(r.month.slice(0, 4), [...(years.get(r.month.slice(0, 4)) ?? []), r]);
  return (
    <Section title={t('childcare_detail.tab_statements')} srTitle
      actions={answer.data.months.some((r) => r.cost) ? <SeriesLink keys={[`childcare:${id}:monthly`]} /> : undefined}>
      <div className="space-y-5">
        {[...years].map(([year, months]) => (
          <section key={year} aria-labelledby={`statements-${year}`} className="space-y-2">
            <h3 id={`statements-${year}`} className="text-sm font-medium text-muted-foreground">{year}</h3>
            <ul aria-label={t('childcare_detail.statements_year', { year })} className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-4">
              {months.map((r) => <li key={r.month}><MonthTile id={id} row={r} missing={missing(r)} /></li>)}
            </ul>
          </section>
        ))}
      </div>
    </Section>
  );
}

const STATE_TONE: Record<string, string> = {
  validated: 'bg-status-good/10 text-status-good-text',
  draft: 'bg-status-warning/10 text-status-warning-text',
};

/** A month's statement: its state, its cost (the server's, « — » before one is drafted), a missing payslip. */
function MonthTile({ id, row, missing }: { id: string; row: StatementRow; missing: boolean }) {
  const { t } = useTranslation();
  const month = new Intl.DateTimeFormat('fr-FR', { month: 'long', timeZone: 'UTC' }).format(new Date(`${row.month}-01T00:00:00Z`));
  return (
    <Link to="/childcare/$id/statements/$month" params={{ id, month: row.month }}
      className={`flex h-full flex-col gap-2 rounded-lg border p-3 transition-colors hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${row.state === 'none' ? 'border-dashed' : ''}`}>
      <span className="flex items-center justify-between gap-2">
        <span className="font-medium capitalize">{month} <span className="sr-only">{row.month.slice(0, 4)}</span></span>{' '}
        <span className={`rounded-full px-2 py-0.5 text-xs ${STATE_TONE[row.state] ?? 'bg-muted text-muted-foreground'}`}>{stateLabel(t, row.state)}</span>
      </span>
      {/* Spaces between the blocks: the link's accessible name reads « septembre 2025 Validé 660,47 € », not one word. */}
      {' '}
      <span className="text-lg font-semibold">{row.cost ? <Money value={row.cost} tabular /> : <span className="text-muted-foreground">{t('childcare_detail.dash')}</span>}</span>
      {' '}
      {missing ? <span className="text-xs text-status-warning-text">{t('childcare_detail.no_payslip')}</span> : null}
    </Link>
  );
}
