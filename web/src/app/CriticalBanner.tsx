import { Link } from '@tanstack/react-router';
import { TriangleAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useNotificationCount } from '@/features/notifications/api';

/** An important notification is said in the shell until it is read. */
export function CriticalBanner() {
  const { t } = useTranslation();
  const critical = useNotificationCount().data?.criticalUnread ?? 0;
  if (critical <= 0) return null;
  return (
    // A pane like the content under it (« jamais sur le fond nu »): the warning tint lies on its frosted surface.
    <div role="alert" className="material rounded-pane mx-2 mb-2 overflow-hidden md:ml-0">
      <div className="flex flex-wrap items-center gap-2 bg-status-warning/10 px-4 py-2">
        <TriangleAlert aria-hidden="true" className="size-4 shrink-0 text-status-warning-text" />
        <span>{t('notifications.critical_banner')}</span>
        <Link to="/notifications" className="underline">{t('notifications.see')}</Link>
      </div>
    </div>
  );
}
