import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { PendingBadge } from '@/features/proposals/PendingBadge';
import { visibleNav } from './nav';
import { useSession } from './Providers';

/** screens.md §1: the sections that do not fit the phone's tab bar. */
export function MorePage() {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold">{t('more.title')}</h1>
      <ul aria-label={t('more.list')} className="divide-y rounded-md border">
        {visibleNav(capabilities).filter((item) => item.mobile === 'more').map((item) => (
          <li key={item.to}>
            <Link to={item.to} className="flex min-h-11 items-center gap-3 px-4 py-3 hover:bg-accent">
              <item.icon aria-hidden="true" className="size-5" />
              <span>{t(item.label)}</span>
              {item.to === '/proposals' ? <PendingBadge /> : null}
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}
