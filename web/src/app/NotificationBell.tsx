import { lazy, Suspense } from 'react';
import { Bell } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { CountBadge } from '@/components/CountBadge';
import { Button } from '@/components/ui/button';
import { useNotificationCount } from '@/features/notifications/api';

// The popover code loads after the first render; the bare button shows meanwhile (200 kB budget).
const BellPopover = lazy(() => import('@/features/notifications/BellPopover').then((m) => ({ default: m.BellPopover })));

/** The header bell: the unread count in the label and a badge; opens the latest to do. */
export function NotificationBell() {
  const { t } = useTranslation();
  const unread = useNotificationCount().data?.unread ?? 0;
  const label = unread > 0 ? t('notifications.bell_count', { count: unread }) : t('notifications.bell');
  const button = (
    <Button type="button" variant="ghost" size="icon" aria-label={label} className="relative">
      <Bell aria-hidden="true" className="size-4.5" />
      {unread > 0 ? <span className="absolute -top-1 -right-1"><CountBadge count={unread} label={label} /></span> : null}
    </Button>
  );
  return <Suspense fallback={button}><BellPopover trigger={button} /></Suspense>;
}
