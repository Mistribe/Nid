import { lazy, Suspense, useState } from 'react';
import { useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import { useApi } from '@/api/ApiProvider';
import type { NannySearch } from '@/app/router';
import { AccessBadge } from '@/components/AccessBadge';
import { ConfirmAction } from '@/components/ConfirmAction';
import { ErrorPage } from '@/components/ErrorPage';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { SharingBadges } from '@/components/ownership/SharingBadges';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { PageHeader } from '@/components/page/PageHeader';
import { RowActions } from '@/components/page/RowActions';
import { Section } from '@/components/page/Section';
import { PageSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { PageTrail } from '@/components/page/trail';
import { useRowDialog } from '@/components/page/useRowDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { invalidateNannies, NANNY_MONTH, useCloseNanny, useNanny, useReopenNanny, type NannyDetail, type NannyMonth } from './nannyApi';
import { NannyMonthDialog } from './NannyMonthDialog';
import { monthTitle } from './statementText';
import '@/locales/childcare';
import '@/locales/deletion';

const DeleteItemFlow = lazy(() => import('@/components/DeleteItemFlow'));

/**
 * #94: a nanny at home, the page grammar of the other detail pages: the year's figures inline (the server's totals), then its
 * months in a table, each recorded or corrected in a dialog. For the current year, how the rest of it counts in the tax estimate.
 */
export function NannyDetailPage() {
  const { t } = useTranslation();
  const { id } = useParams({ from: '/childcare/nannies/$id' });
  const search: NannySearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const answer = useNanny(id, search.year ? Number(search.year) : undefined);
  const close = useCloseNanny();
  const reopen = useReopenNanny();
  const [deleting, setDeleting] = useState(false);
  const crumbs = [{ label: t('childcare_list.title'), to: '/childcare' as const }];
  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <><PageTrail crumbs={crumbs} /><PageSkeleton label={t('childcare_nanny.loading')} actions={2} stats={4} tabs={0} /></>;

  const detail = answer.data;
  const { summary } = detail;
  const params = { params: { path: { id } } };
  const pending = close.isPending || reopen.isPending;
  const error = close.error ?? reopen.error;
  const sharing = <SharingMenu item={id} onAnnounce={toast.success} shared={summary.visibility === 'shared'} label={t('page.more_actions')}
    trailing={[{ label: t('deletion.menu'), destructive: true, onSelect: () => setDeleting(true) }]} />;
  const actions = !summary.writable ? null : summary.closed
    ? <><Button type="button" variant="outline" disabled={pending} onClick={() => reopen.mutate(params)}>{t('childcare_nanny.reopen')}</Button>{sharing}</>
    : <><ConfirmAction label={t('childcare_nanny.close')} confirmation={t('childcare_nanny.close_confirmation', { name: summary.name })} pending={pending} onConfirm={() => close.mutate(params)} />{sharing}</>;

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} title={summary.name} actions={actions}
        meta={(
          <>
            <span className="text-sm text-muted-foreground">{t('childcare_nanny.kind')}</span>
            <AccessBadge access={summary.visibility === 'private' ? 'private' : 'shared'} /><SharingBadges item={id} />
            {summary.closed ? <Badge variant="secondary">{t('childcare_nanny.closed_badge')}</Badge> : null}
            <OwnerChips owners={summary.owners} />
          </>
        )}>
        <section aria-label={t('childcare_nanny.figures', { year: detail.year })}>
          <StatLine items={[
            { label: t('childcare_nanny.paid'), value: <Money value={detail.paid} tabular /> },
            { label: t('childcare_nanny.cmg'), value: <Money value={detail.cmg} tabular /> },
            { label: t('childcare_nanny.net_cost'), tip: t('childcare_nanny.net_cost_tip'), value: <Money value={detail.netCost} tabular /> },
            { label: t('childcare_nanny.months_recorded'), value: t('childcare_nanny.months', { count: detail.months.length }) },
            ...(detail.projection ? [{
              label: t('childcare_nanny.year_estimate'), tip: t('childcare_nanny.year_estimate_tip'),
              value: <Money value={detail.projection.total} tabular />,
              hint: <>{t('childcare_nanny.year_estimate_hint', { count: detail.projection.months })} <Money value={detail.projection.mean} tabular /></>,
            }] : []),
          ]} />
        </section>
      </PageHeader>
      {summary.writable ? null : <p>{t('childcare_nanny.read_only')}</p>}
      {deleting ? <Suspense fallback={null}><DeleteItemFlow kind="childcare.delete_nanny" id={id} closed={summary.closed} onClose={() => setDeleting(false)} /></Suspense> : null}
      {error ? <ProblemMessage problem={present(toProblem(error))} /> : null}
      <Field label={t('childcare_nanny.year')}>
        {(c) => (
          <div className="max-w-40">
            <SelectField {...c} value={String(detail.year)} options={detail.years.map((y) => ({ value: String(y), label: String(y) }))}
              onValueChange={(year) => void navigate({ to: '/childcare/nannies/$id', params: { id }, search: { year } })} />
          </div>
        )}
      </Field>
      <Months detail={detail} writable={summary.writable && !summary.closed} />
    </article>
  );
}

function Months({ detail, writable }: { detail: NannyDetail; writable: boolean }) {
  const { t } = useTranslation();
  const id = detail.summary.id;
  const rows = detail.months.map((m) => ({ ...m, id: m.month }));
  const row = useRowDialog(rows, 'nanny-month-menu');
  const actions = (m: NannyMonth) => (
    <MonthActions nannyId={id} month={m} onEdit={() => row.open({ ...m, id: m.month })} onDeleted={row.announce} onReloaded={() => row.headingRef.current?.focus()} />
  );
  return (
    <Section title={t('childcare_nanny.months_title', { year: detail.year })} headingRef={row.headingRef}>
      {writable ? <Button ref={row.addButtonRef} type="button" variant="outline" onClick={() => row.open()}>{t('childcare_nanny.month.add')}</Button> : null}
      {writable && row.dialog ? (
        <NannyMonthDialog nannyId={id} year={detail.year} month={row.dialog.editing} onClose={row.cancel} onDone={row.saved} />
      ) : null}
      {rows.length === 0 ? <p className="text-muted-foreground">{t('childcare_nanny.months_empty')}</p> : (
        <SortableTable<NannyMonth> caption={t('childcare_nanny.months_title', { year: detail.year })} rows={rows} rowKey={(m) => m.month}
          columns={[
            { id: 'month', header: t('childcare_nanny.month.month'), sort: (m) => m.month, cell: (m) => monthTitle(m.month) },
            { id: 'paid', header: t('childcare_nanny.paid'), align: 'end', sort: (m) => amountKey(m.paid.amount), cell: (m) => <Money value={m.paid} tabular /> },
            { id: 'cmg', header: t('childcare_nanny.cmg'), align: 'end', sort: (m) => amountKey(m.cmg.amount), cell: (m) => <Money value={m.cmg} tabular /> },
            { id: 'net', header: t('childcare_nanny.net_cost'), align: 'end', sort: (m) => amountKey(m.netCost.amount), cell: (m) => <Money value={m.netCost} tabular /> },
            { id: 'hours', header: t('childcare_nanny.month.hours'), align: 'end', cell: (m) => m.hours ? t('childcare_nanny.hours', { hours: m.hours.replace('.', ',') }) : '—' },
            ...(writable ? [{ id: 'menu', header: t('childcare_nanny.actions'), align: 'end' as const, cell: actions }] : []),
          ]}
          mobile={(m) => (
            <div className="flex items-center justify-between gap-2">
              <span>{monthTitle(m.month)} · <Money value={m.netCost} tabular /></span>{writable ? actions(m) : null}
            </div>
          )} />
      )}
    </Section>
  );
}

/** A month's `⋯`: correct it in the dialog, or delete it after a confirmation; a refusal shows on this row only. */
function MonthActions({ nannyId, month, onEdit, onDeleted, onReloaded }: {
  nannyId: string; month: NannyMonth; onEdit: () => void; onDeleted: (message: string) => void; onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const title = monthTitle(month.month);
  const remove = $api.useMutation('delete', NANNY_MONTH, {
    onSuccess: () => { void invalidateNannies(queryClient); onDeleted(t('childcare_nanny.month.deleted', { month: title })); },
  });
  return (
    <RowActions menuId={`nanny-month-menu-${month.month}`} menuLabel={t('childcare_nanny.month.menu', { month: title })}
      items={[{ label: t('childcare_nanny.month.edit'), onSelect: onEdit }]}
      confirmation={t('childcare_nanny.month.delete_confirm', { month: title })} remove={remove}
      present={(_, error) => present(toProblem(error))} invalidate={invalidateNannies} onReloaded={onReloaded}
      onConfirm={() => remove.mutate({ params: { path: { id: nannyId, month: month.month } } })} />
  );
}
