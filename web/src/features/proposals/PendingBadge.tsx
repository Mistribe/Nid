import { useTranslation } from 'react-i18next';
import { usePendingCount } from './api';

/** Spec D3: the count beside « À valider »; nothing at zero. Its text is also the link's accessible description. */
export function PendingBadge() {
  const { t } = useTranslation();
  const count = usePendingCount();
  if (!count) return null;
  return (
    <span className="ml-auto rounded-full bg-primary px-2 text-xs text-primary-foreground">
      <span aria-hidden="true">{count}</span>
      <span className="sr-only">{t('proposals.badge', { count })}</span>
    </span>
  );
}
