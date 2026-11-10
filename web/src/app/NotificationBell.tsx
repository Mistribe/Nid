import { lazy, Suspense } from 'react';
import { Bell } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { CountBadge } from '@/components/CountBadge';
import { Button } from '@/components/ui/button';
import { useNotificationCount } from '@/features/notifications/api';
import { cn } from '@/lib/utils';

// The popover code loads after the first render; the bare button shows meanwhile (200 kB budget).
const BellPopover = lazy(() => import('@/features/notifications/BellPopover').then((m) => ({ default: m.BellPopover })));

/**
 * The header bell: the unread count in the label and a badge; opens the latest to do. While an important notification is
 * unread, the badge turns to the warning colour on a light halo and the label says how many: no banner under the bar.
 */
export function NotificationBell() {
  const { t } = useTranslation();
  const count = useNotificationCount().data;
  const unread = count?.unread ?? 0;
  const critical = Math.min(count?.criticalUnread ?? 0, unread);
  const label = unread > 0
    ? [t('notifications.bell_count', { count: unread }), critical > 0 ? t('notifications.bell_critical', { count: critical }) : null].filter(Boolean).join(', ')
    : t('notifications.bell');
  const button = (
    <Button type="button" variant="ghost" size="icon" aria-label={label} className={cn('relative', critical > 0 && 'bg-status-warning/15 hover:bg-status-warning/25')}>
      <Bell aria-hidden="true" className="size-4.5" />
      {unread > 0 ? <span className="absolute -top-1 -right-1"><CountBadge count={unread} label={label} warning={critical > 0} /></span> : null}
    </Button>
  );
  return <Suspense fallback={button}><BellPopover trigger={button} /></Suspense>;
}
