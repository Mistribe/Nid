import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';

/** Never says whether the thing is absent or hidden from you (architecture.md §5). */
export function NotFoundPage() {
  const { t } = useTranslation();
  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold">{t('problem.not_found')}</h1>
      <Link to="/" className="underline">{t('nav.home')}</Link>
    </div>
  );
}
