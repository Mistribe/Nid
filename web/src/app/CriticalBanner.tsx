import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useNotificationCount } from '@/features/notifications/api';

/** An important notification is said in the shell until it is read. */
export function CriticalBanner() {
  const { t } = useTranslation();
  const critical = useNotificationCount().data?.criticalUnread ?? 0;
  if (critical <= 0) return null;
  return (
    <div role="alert" className="flex flex-wrap items-center gap-2 border-b bg-status-warning/10 px-4 py-2">
      <span>{t('notifications.critical_banner')}</span>
      <Link to="/notifications" className="underline">{t('notifications.see')}</Link>
    </div>
  );
}
