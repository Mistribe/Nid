import { Link } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';

/**
 * #720: the cards attached to no account, whose lines the spending figures leave out (as the server says); each links to its « Régler »,
 * which offers the account the card most likely belongs to.
 */
export function UnattachedCards({ cards }: { cards: { id: string; name: string }[] }) {
  const { t } = useTranslation();
  if (cards.length === 0) return null;
  return (
    <p role="note" className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm">
      <AlertTriangle aria-hidden="true" className="size-4 shrink-0 text-status-warning" />
      <span>{t('dashboard.month.unattached', { count: cards.length })}</span>
      {cards.map((c) => (
        <Link key={c.id} to="/settings/banks" search={{ account: c.id }} className="font-medium underline underline-offset-4">
          {t('dashboard.month.attach', { name: c.name })}
        </Link>
      ))}
    </p>
  );
}
