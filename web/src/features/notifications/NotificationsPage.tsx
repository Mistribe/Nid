import { Link } from '@tanstack/react-router';
import { CircleAlertIcon, InfoIcon, OctagonAlertIcon } from 'lucide-react';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import '@/locales/notifications';
import { useDismiss, useNotifications, useRead, useReadAll, useSnooze, type NotificationList, type NotificationView } from './api';
import { localDateOf, parseNotificationLink, readsOnFollow } from './link';

const ConfirmDialog = lazy(() => import('@/components/ConfirmDialog'));
const SnoozeDialog = lazy(() => import('./SnoozeDialog').then((m) => ({ default: m.SnoozeDialog })));

const TABS = ['todo', 'snoozed', 'history'] as const;
const LEVELS = { critical: OctagonAlertIcon, action: CircleAlertIcon, info: InfoIcon } as const;
const LEVEL_COLOR = { critical: 'text-destructive', action: 'text-status-warning', info: 'text-muted-foreground' } as const;

type Actions = {
  read: (n: NotificationView) => void; week: (n: NotificationView) => void; month: (n: NotificationView) => void;
  until: (n: NotificationView) => void; dismiss: (n: NotificationView) => void;
};

function Level({ level }: { level: string }) {
  const { t } = useTranslation();
  const key = level in LEVELS ? (level as keyof typeof LEVELS) : 'info';
  const Icon = LEVELS[key];
  return <span className="inline-flex items-center gap-1.5"><Icon aria-hidden="true" className={`size-4 ${LEVEL_COLOR[key]}`} />{t(`notifications_page.level.${key}`)}</span>;
}

/** Following the link of an unread, live notification reads it. */
function Element({ n, onFollow }: { n: NotificationView; onFollow: (n: NotificationView) => void }) {
  if (!n.link) return <>{n.body}</>;
  const target = parseNotificationLink(n.link);
  return <Link to={target.to as never} search={target.search as never} onClick={() => { if (readsOnFollow(n)) onFollow(n); }} className="underline">{n.body}</Link>;
}

function Panel({ list, actions }: { list: NotificationList; actions: Actions }) {
  const { t } = useTranslation();
  const answer = useNotifications(list);
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <div role="status" aria-busy="true" aria-label={t('notifications_page.loading')} className="h-40 shimmer rounded-md border bg-muted" />;
  const rows = answer.data.pages.flatMap((page) => page.entries);
  if (rows.length === 0) return <EmptyState module="/notifications" title={t(`notifications_page.empty.${list}`)} body="" />;

  const menu = (n: NotificationView) => {
    if (list === 'history') return null;
    const items = [
      ...(n.read ? [] : [{ label: t('notifications_page.read'), onSelect: () => actions.read(n) }]),
      ...(n.critical ? [] : [
        {
          label: t('notifications_page.snooze'), items: [
            { label: t('notifications_page.week'), onSelect: () => actions.week(n) },
            { label: t('notifications_page.month'), onSelect: () => actions.month(n) },
            { label: t('notifications_page.until'), onSelect: () => actions.until(n) },
          ],
        },
        { label: t('notifications_page.dismiss'), onSelect: () => actions.dismiss(n), destructive: true },
      ]),
    ];
    return <RowMenu label={t('notifications_page.menu')} items={items} />;
  };
  const title = (n: NotificationView) => <span className={n.read ? undefined : 'font-semibold'}>{n.title}</span>;
  const arrived = (n: NotificationView) => <LocalDate value={localDateOf(n.arrivedAt)} style="short" />;
  const due = (n: NotificationView) => (n.dueOn ? <LocalDate value={n.dueOn} style="short" /> : null);
  const until = (n: NotificationView) => (n.snoozedUntil ? <LocalDate value={n.snoozedUntil} /> : null);

  return (
    <>
    <SortableTable caption={t(`notifications_page.table.${list}`)} rows={rows} rowKey={(n) => n.id}
      columns={[
        { id: 'level', header: t('notifications_page.column.level'), cell: (n) => <Level level={n.level} /> },
        { id: 'title', header: t('notifications_page.column.title'), cell: title },
        { id: 'element', header: t('notifications_page.column.element'), cell: (n) => <Element n={n} onFollow={actions.read} /> },
        { id: 'due', header: t('notifications_page.column.due'), cell: due, sort: (n) => n.dueOn ?? '' },
        { id: 'arrived', header: t('notifications_page.column.arrived'), cell: arrived, sort: (n) => n.arrivedAt },
        ...(list === 'snoozed' ? [{ id: 'until', header: t('notifications_page.column.until'), cell: until, sort: (n: NotificationView) => n.snoozedUntil ?? '' }] : []),
        ...(list === 'history' ? [] : [{ id: 'actions', header: t('notifications_page.column.actions'), cell: menu, align: 'end' as const }]),
      ]}
      mobile={(n) => (
        <div className="space-y-1">
          <div className="flex items-start justify-between gap-2"><div className="text-sm"><Level level={n.level} /></div>{menu(n)}</div>
          <div>{title(n)}</div>
          <div className="text-sm"><Element n={n} onFollow={actions.read} /></div>
          <div className="text-xs text-muted-foreground">
            {n.dueOn ? <>{t('notifications_page.column.due')} {due(n)} · </> : null}
            {t('notifications_page.column.arrived')} {arrived(n)}
            {list === 'snoozed' && n.snoozedUntil ? <> · {t('notifications_page.column.until')} {until(n)}</> : null}
          </div>
        </div>
      )} />
      {answer.hasNextPage ? (
        <Button type="button" variant="outline" disabled={answer.isFetchingNextPage} onClick={() => void answer.fetchNextPage()}>{t('notifications_page.more')}</Button>
      ) : null}
    </>
  );
}

/** The notification centre: what waits, what is snoozed, the last 90 days (notifications design §6.2). */
export function NotificationsPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useDetailTab(TABS);
  const readAll = useReadAll();
  const read = useRead();
  const snooze = useSnooze();
  const dismiss = useDismiss();
  const [snoozing, setSnoozing] = useState<NotificationView | null>(null);
  const [dismissing, setDismissing] = useState<NotificationView | null>(null);

  const failure = [readAll, read, snooze, dismiss].map((m) => m.error).find((e) => e != null);
  const actions: Actions = {
    read: (n) => read.mutate({ params: { path: { id: n.id } } }),
    week: (n) => snooze.mutate({ params: { path: { id: n.id } }, body: { preset: 'week' } }),
    month: (n) => snooze.mutate({ params: { path: { id: n.id } }, body: { preset: 'month' } }),
    until: (n) => setSnoozing(n),
    dismiss: (n) => setDismissing(n),
  };

  return (
    <article className="space-y-6">
      <PageHeader title={t('notifications_page.title')}
        actions={<Button type="button" variant="outline" disabled={readAll.isPending} onClick={() => readAll.mutate({})}>{t('notifications_page.read_all')}</Button>} />
      {failure ? <ProblemMessage problem={present(toProblem(failure))} /> : null}
      {snoozing ? (
        <Suspense fallback={null}>
          <SnoozeDialog id={snoozing.id} onClose={() => setSnoozing(null)} onDone={(message) => { setSnoozing(null); toast.success(message); }} />
        </Suspense>
      ) : null}
      {dismissing ? (
        <Suspense fallback={null}>
          <ConfirmDialog label={t('notifications_page.dismiss')} confirmation={t('notifications_page.dismiss_confirmation')} pending={dismiss.isPending}
            onClose={() => setDismissing(null)}
            onConfirm={() => { const n = dismissing; setDismissing(null); dismiss.mutate({ params: { path: { id: n.id } } }); }} />
        </Suspense>
      ) : null}
      <DetailTabs value={tab} onChange={setTab} tabs={TABS.map((id) => ({
        id, label: t(`notifications_page.tab.${id}`), content: <Panel list={id} actions={actions} />,
      }))} />
    </article>
  );
}
