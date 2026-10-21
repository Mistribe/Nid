import { useTranslation } from 'react-i18next';
import type { TFunction } from 'i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { Terms } from '../../loans/detail/Terms';
import { money, percent, signedMoney } from '../../loans/format';
import type { PropertyDetail } from '../api';
import '@/locales/properties';

/**
 * The last 12 months as the server computed them — rates its fractions in %, arrears signed (the sign read from the
 * string, never computed). `since` is only for a rented property.
 */
export function rentalFigures(t: TFunction, property: PropertyDetail) {
  const f = property.figures;
  const m = (amount: string) => money(property.currency, amount);
  const arrears = signedMoney(property.currency, f.arrears);
  // No yield before 12 months of rental (3C2 §4): the server gives none.
  const yieldText = (rate: string | null | undefined) => (rate ? percent(t, rate) : t('property_rental.not_significant'));
  return {
    since: f.rentedSince ? formatLocalDate(f.rentedSince) : '',
    expected: m(f.expectedRent),
    received: m(f.rentReceived),
    arrears: f.arrears.startsWith('-') ? t('property_rental.arrears_ahead', { amount: arrears }) : arrears,
    vacancy: f.vacantDays == null || !f.vacancyRate
      ? t('property_detail.unknown')
      : t('property_rental.vacancy_days', { count: f.vacantDays, rate: percent(t, f.vacancyRate) }),
    gross: yieldText(f.grossYield),
    net: yieldText(f.netYieldBeforeTax),
  };
}

/** Spec 3D2 §3: the rental start, then the 12 months. Without a rental start: « Ce bien n’est pas loué. ». */
export function RentalFigures({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  if (!property.figures.rentedSince) return <p>{t('property_rental.not_rented')}</p>;
  const r = rentalFigures(t, property);
  const items: [string, string][] = [
    [t('property_rental.rented_since'), r.since],
    [t('property_rental.expected_rent'), r.expected],
    [t('property_rental.rent_received'), r.received],
    [t('property_rental.arrears'), r.arrears],
    [t('property_rental.vacancy'), r.vacancy],
    [t('property_rental.gross_yield'), r.gross],
    [t('property_rental.net_yield'), r.net],
  ];
  return <Terms items={items} />;
}
