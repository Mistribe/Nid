import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { money } from '../../loans/format';
import type { PropertyDetail } from '../api';

/** Spec §6: the readable linked loans, each with its balance, and in the property's currency when it differs. */
export function LinkedLoans({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  if (property.loans.length === 0) return <p className="text-muted-foreground">{t('property_detail.no_loan')}</p>;
  return (
    <ul className="space-y-1">
      {property.loans.map((l) => (
        <li key={l.id} className="flex flex-wrap items-baseline gap-x-3 tabular-nums">
          <Link to="/loans/$id" params={{ id: l.id }} className="underline">{l.name}</Link>
          <span>{t('property_detail.loan_balance', { amount: money(l.currency, l.balance) })}</span>
          {l.currency === property.currency ? null : (
            <span>
              {l.balanceConverted
                ? t('property_detail.loan_converted', { amount: money(property.currency, l.balanceConverted) })
                : t('property_detail.rate_missing')}
            </span>
          )}
        </li>
      ))}
    </ul>
  );
}
