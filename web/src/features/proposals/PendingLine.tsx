import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { usePendingCount } from '@/api/proposals';

/** Spec 4C2 §5: « N propositions à valider », to the list; nothing at zero. */
export function PendingLine() {
  const { t } = useTranslation();
  const count = usePendingCount();
  if (!count) return null;
  return <Link to="/proposals" className="underline">{t('proposals.line', { count })}</Link>;
}
