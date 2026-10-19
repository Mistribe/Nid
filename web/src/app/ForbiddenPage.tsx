import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/components/page/PageHeader';
import { buttonVariants } from '@/components/ui/button';

/** Never says whether the thing is absent or hidden from you (architecture.md §5). */
export function ForbiddenPage() {
  const { t } = useTranslation();
  return (
    <div className="space-y-4">
      <PageHeader title={t('problem.forbidden')} />
      <Link to="/" className={buttonVariants({ variant: 'outline' })}>{t('nav.home')}</Link>
    </div>
  );
}
