import { Link } from '@tanstack/react-router';
import { Sparkles } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/components/page/PageHeader';
import { Button } from '@/components/ui/button';
import { PendingBadge } from '@/features/proposals/PendingBadge';
import { visibleNav } from './nav';
import { useSession } from './Providers';

/** screens.md §1: the sections that do not fit the phone's tab bar. */
export function MorePage() {
  const { t } = useTranslation();
  const { capabilities, signOut } = useSession();
  return (
    <div className="space-y-4">
      <PageHeader title={t('more.title')} />
      <ul aria-label={t('more.list')} className="divide-y rounded-lg border">
        {visibleNav(capabilities).filter((item) => item.mobile === 'more').map((item) => (
          <li key={item.to}>
            <Link to={item.to} className="flex min-h-11 items-center gap-3 px-4 py-3 hover:bg-accent">
              <item.icon aria-hidden="true" className="size-5" />
              <span>{t(item.label)}</span>
              {item.to === '/proposals' ? <PendingBadge /> : null}
            </Link>
          </li>
        ))}
        <li>
          <Link to="/changelog" className="flex min-h-11 items-center gap-3 px-4 py-3 hover:bg-accent">
            <Sparkles aria-hidden="true" className="size-5" />
            <span>{t('app.changelog.title')}</span>
          </Link>
        </li>
      </ul>
      <Button variant="outline" className="md:hidden" onClick={() => void signOut()}>{t('shell.sign_out')}</Button>
    </div>
  );
}
