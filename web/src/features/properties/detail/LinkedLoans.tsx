import { useTranslation } from 'react-i18next';
import { tileOf } from '@/app/nav';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { money } from '../../loans/format';
import type { PropertyDetail } from '../api';

/** Spec §6: the readable linked loans as a grouped list, each with its balance, and in the property's currency when it differs. */
export function LinkedLoans({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  if (property.loans.length === 0) return <p className="text-muted-foreground">{t('property_detail.no_loan')}</p>;
  return (
    <GroupedList>
      {property.loans.map((l) => (
        <GroupedRow key={l.id} link={{ to: '/loans/$id', params: { id: l.id } }} tile={tileOf('/loans')} title={l.name}
          detail={[
            t('property_detail.loan_balance', { amount: money(l.currency, l.balance) }),
            l.currency === property.currency ? null
              : l.balanceConverted ? t('property_detail.loan_converted', { amount: money(property.currency, l.balanceConverted) }) : t('property_detail.rate_missing'),
          ].filter(Boolean).join(' · ')} />
      ))}
    </GroupedList>
  );
}
