import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { Terms } from '../../loans/detail/Terms';
import { money, percent, signedMoney } from '../../loans/format';
import type { PropertyDetail } from '../api';

/**
 * Spec 3D2 §3: the rental start, then the last 12 months as the server computed them — rates its fractions in %, arrears
 * signed (the sign read from the string, never computed). Without a rental start: « Ce bien n’est pas loué. ».
 */
export function RentalFigures({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const f = property.figures;
  if (!f.rentedSince) return <p>{t('property_rental.not_rented')}</p>;

  const m = (amount: string) => money(property.currency, amount);
  const arrears = signedMoney(property.currency, f.arrears);
  // No yield before 12 months of rental (3C2 §4): the server gives none.
  const yieldText = (rate: string | null | undefined) => (rate ? percent(t, rate) : t('property_rental.not_significant'));
  const items: [string, string][] = [
    [t('property_rental.rented_since'), formatLocalDate(f.rentedSince)],
    [t('property_rental.expected_rent'), m(f.expectedRent)],
    [t('property_rental.rent_received'), m(f.rentReceived)],
    [t('property_rental.arrears'), f.arrears.startsWith('-') ? t('property_rental.arrears_ahead', { amount: arrears }) : arrears],
    [t('property_rental.vacancy'), f.vacantDays == null || !f.vacancyRate
      ? t('property_detail.unknown')
      : t('property_rental.vacancy_days', { count: f.vacantDays, rate: percent(t, f.vacancyRate) })],
    [t('property_rental.gross_yield'), yieldText(f.grossYield)],
    [t('property_rental.net_yield'), yieldText(f.netYieldBeforeTax)],
  ];
  return <Terms items={items} />;
}
