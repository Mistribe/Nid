import { useParams } from '@tanstack/react-router';
import { useState } from 'react';
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
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ErrorPage } from '@/components/ErrorPage';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Segmented } from '@/components/form/Segmented';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useAnchoredDocuments } from '@/features/documents/api';
import { ItemDocuments } from '@/features/documents/ItemDocuments';
import { useChildcare, useOverrideWeek, useReopenChildcare, useStatements, type ChildcareDetail, type ChildcareTerms, type StatementRow } from './api';
import { EditChildcareDialog, EndDialog, GapsDialog, ReviseDialog } from './ChildcareDialogs';
import { dayName } from './childcareSchema';
import { monthTitle, stateLabel } from './statementText';
import { YearSummary } from './YearSummary';
import '@/locales/childcare';
import '@/locales/forms';
import { PageSkeleton } from '@/components/page/skeletons';

type Open = 'edit' | 'revise' | 'gaps' | 'end' | null;

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
    items={summary.closed ? [] : [{ label: t('childcare_detail.end'), onSelect: show('end') }]} />;
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
      <PageHeader crumbs={crumbs} title={summary.name} actions={actions}
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
            // #385: no estimate (« — ») until a validated month carries a CMG; the server sends none rather than 0.
            detail.cmgEstimate
              ? { label: t('childcare_detail.cmg_estimate'), value: <Money value={detail.cmgEstimate} tabular /> }
              : { label: t('childcare_detail.cmg_estimate'), value: t('childcare_detail.dash'), hint: t('childcare_detail.cmg_estimate_none') },
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
      <Tabs detail={detail} terms={terms} onGaps={summary.canWrite && !summary.closed ? show('gaps') : null} />
    </article>
  );
}

function Tabs({ detail, terms, onGaps }: { detail: ChildcareDetail; terms: ChildcareTerms; onGaps: (() => void) | null }) {
  const { t } = useTranslation();
  const tabs = [
    { id: 'statements', label: t('childcare_detail.tab_statements'), content: <Statements id={detail.summary.id} /> },
    { id: 'summary', label: t('childcare_detail.tab_summary'), content: <YearSummary id={detail.summary.id} /> },
    { id: 'planning', label: t('childcare_detail.tab_planning'), content: <Planning detail={detail} terms={terms} /> },
    { id: 'gaps', label: t('childcare_form.step_gaps'), content: <Gaps detail={detail} onEdit={onGaps} /> },
    { id: 'revisions', label: t('childcare_detail.tab_revisions'), content: <Revisions terms={detail.terms} inForceFrom={detail.summary.inForceFrom} /> },
    { id: 'documents', label: t('childcare_detail.tab_documents'), content: <ItemDocuments itemId={detail.summary.id} canWrite={detail.summary.canWrite} defaultRole="payslip" /> },
  ];
  const [tab, setTab] = useDetailTab(tabs.map((x) => x.id));
  return <DetailTabs value={tab} onChange={setTab} tabs={tabs} />;
}

function Planning({ detail, terms }: { detail: ChildcareDetail; terms: ChildcareTerms }) {
  const { t } = useTranslation();
  const several = terms.weekTypes.length > 1;
  return (
    <div className="space-y-4">
      <StatLine items={[
        { label: t('childcare_form.net'), value: terms.netHourlyRate.replace('.', ',') },
        { label: t('childcare_form.upkeep'), value: terms.upkeepPer9h.replace('.', ',') },
        { label: t('childcare_form.meal_price'), value: terms.mealPrice ? terms.mealPrice.replace('.', ',') : t('childcare_detail.dash') },
        { label: t('childcare_form.overtime'), value: terms.overtimePct.replace('.', ',') },
        { label: t('childcare_detail.since'), value: <LocalDate value={terms.from} style="short" /> },
      ]} />
      {terms.weekTypes.map((w) => (
        <div key={w.name} className="space-y-1">
          {several ? <h3 className="text-sm font-medium">{t('childcare_detail.week_type', { name: w.name, hours: hoursText(w.weeklyHours), count: w.weeks })}</h3> : null}
          <ul aria-label={several ? w.name : t('childcare_detail.template')} className="space-y-1">
            {w.schedule.map((s) => (
              <li key={s.day}>{t('childcare_detail.slot', { day: dayName(t, s.day), arrival: s.arrival, departure: s.departure })}{s.meal ? t('childcare_detail.with_meal') : ''}</li>
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

function Revisions({ terms, inForceFrom }: { terms: ChildcareTerms[]; inForceFrom: string }) {
  const { t } = useTranslation();
  // Only the last terms are open-ended: in force, or a revision that has not started yet.
  const end = (r: ChildcareTerms) => r.to ? <LocalDate value={r.to} style="short" /> : t(r.from > inForceFrom ? 'childcare_detail.upcoming' : 'childcare_detail.current');
  return (
    <SortableTable<ChildcareTerms> caption={t('childcare_detail.tab_revisions')} rows={[...terms].reverse()} rowKey={(r) => r.from}
      columns={[
        { id: 'from', header: t('childcare_detail.col_from'), sort: (r) => r.from, cell: (r) => <LocalDate value={r.from} style="short" /> },
        { id: 'to', header: t('childcare_detail.col_to'), sort: (r) => r.to ?? '', cell: end },
        { id: 'rate', header: t('childcare_form.net'), align: 'end', cell: (r) => r.netHourlyRate.replace('.', ',') },
        { id: 'pay', header: t('childcare_list.col_pay'), align: 'end', cell: (r) => <Money value={r.monthlyBasePay} tabular /> },
      ]}
      mobile={(r) => (
        <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
          <span><LocalDate value={r.from} style="short" /> → {end(r)}</span>
          <Money value={r.monthlyBasePay} tabular />
        </div>
      )} />
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
  const state = (r: StatementRow) => stateLabel(t, r.state);
  return (
    <SortableTable<StatementRow> caption={t('childcare_detail.tab_statements')} rows={answer.data.months} rowKey={(r) => r.month}
      rowLink={(r) => ({ to: '/childcare/$id/statements/$month', params: { id, month: r.month } })}
      columns={[
        { id: 'month', header: t('childcare_detail.col_month'), sort: (r) => r.month, cell: (r) => monthTitle(r.month) },
        { id: 'state', header: t('childcare_detail.col_state'), sort: state, cell: (r) => (
          <>{state(r)}{missing(r) ? <Badge variant="outline" className="ml-2">{t('childcare_detail.no_payslip')}</Badge> : null}</>
        ) },
        { id: 'cost', header: t('childcare_detail.col_cost'), align: 'end', cell: (r) => (r.cost ? <Money value={r.cost} tabular /> : t('childcare_detail.dash')) },
      ]}
      mobile={(r) => (
        <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
          <span>{monthTitle(r.month)} · {state(r)}{missing(r) ? ` · ${t('childcare_detail.no_payslip')}` : ''}</span>
          {r.cost ? <Money value={r.cost} tabular /> : null}
        </div>
      )} />
  );
}
