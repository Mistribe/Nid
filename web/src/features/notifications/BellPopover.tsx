import { useState, type ReactNode } from 'react';
import { Link } from '@tanstack/react-router';
import { TriangleAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/utils';
import { LocalDate } from '@/components/LocalDate';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import '@/locales/notifications';
import { useLatestNotifications, useNotificationCount, useRead, useReadAll } from './api';
import { localDateOf, parseNotificationLink, readsOnFollow } from './link';

const LATEST = 8;

function Entries({ onNavigate }: { onNavigate: () => void }) {
  const { t } = useTranslation();
  const waiting = (useNotificationCount().data?.criticalUnread ?? 0) > 0;
  const list = useLatestNotifications('todo', LATEST);
  // The unread important ones the bell's warning badge points at, asked apart: they may be older than the latest to do.
  const critical = useLatestNotifications('critical', LATEST, waiting);
  const readAll = useReadAll();
  const read = useRead();
  const urgent = (entry: { critical: boolean; read: boolean }) => entry.critical && !entry.read;
  const first = waiting ? (critical.data?.entries ?? []) : [];
  const ids = new Set(first.map((e) => e.id));
  const entries = [...first, ...(list.data?.entries ?? []).filter((e) => !ids.has(e.id))].slice(0, LATEST);
  return (
    <>
      <div className="flex items-center justify-between gap-2 border-b px-3 py-2">
        <h2 id="notification-popover-title" className="text-sm font-semibold">{t('notification_popover.title')}</h2>
        <Button type="button" variant="ghost" size="sm" onClick={() => readAll.mutate({})} disabled={readAll.isPending || entries.length === 0}>
          {t('notification_popover.read_all')}
        </Button>
      </div>
      {entries.length === 0 ? (
        <p className="px-3 py-4 text-sm text-muted-foreground">{t('notification_popover.empty')}</p>
      ) : (
        <ul className="max-h-96 divide-y overflow-y-auto">
          {entries.map((entry) => {
            const target = entry.link ? parseNotificationLink(entry.link) : null;
            return (
              <li key={entry.id}>
                <Link to={(target?.to ?? '/notifications') as never} search={(target?.search ?? {}) as never}
                  onClick={() => { if (readsOnFollow(entry)) read.mutate({ params: { path: { id: entry.id } } }); onNavigate(); }}
                  className={cn('block px-3 py-2 text-sm outline-none hover:bg-accent focus-visible:bg-accent',
                    urgent(entry) && 'border-l-3 border-status-warning bg-status-warning/10')}>
                  {urgent(entry) ? (
                    <span className="flex items-center gap-1 text-xs font-semibold text-status-warning-text">
                      <TriangleAlert aria-hidden="true" className="size-3.5" />{t('notification_popover.critical')}
                    </span>
                  ) : null}
                  <span className={entry.read ? 'block' : 'block font-semibold'}>{entry.title}</span>
                  <span className="block text-muted-foreground">{entry.body}</span>
                  {/* No relative-time helper in the app: the arrival date is shown instead. */}
                  <span className="block text-xs text-muted-foreground">
                    {t('notification_popover.arrived')} <LocalDate value={localDateOf(entry.arrivedAt)} style="short" />
                  </span>
                </Link>
              </li>
            );
          })}
        </ul>
      )}
      <div className="border-t px-3 py-2 text-sm">
        <Link to="/notifications" onClick={onNavigate} className="underline">{t('notification_popover.see_all')}</Link>
      </div>
    </>
  );
}

/** The bell popover: the eight latest to do. Lazy, so Radix popover stays out of the initial bundle; `trigger` is the bell. */
export function BellPopover({ trigger }: { trigger: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>{trigger}</PopoverTrigger>
      <PopoverContent align="end" aria-labelledby="notification-popover-title" className="w-96 max-w-[calc(100vw-2rem)] p-0">
        <Entries onNavigate={() => setOpen(false)} />
      </PopoverContent>
    </Popover>
  );
}
