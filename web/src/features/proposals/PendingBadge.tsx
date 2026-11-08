import { useTranslation } from 'react-i18next';
import { CountBadge } from '@/components/CountBadge';
import { usePendingCount } from '@/api/proposals';

/** Spec D3: the count beside « À valider »; nothing at zero. Its text is also the link's accessible description. */
export function PendingBadge({ dot = false }: { dot?: boolean }) {
  const { t } = useTranslation();
  const count = usePendingCount();
  if (!count) return null;
  return <CountBadge count={count} label={t('proposals.badge', { count })} dot={dot} />;
}
