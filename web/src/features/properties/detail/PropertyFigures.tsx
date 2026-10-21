import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { StatLine } from '@/components/page/StatLine';
import { Terms } from '../../loans/detail/Terms';
import { money, signedMoney } from '../../loans/format';
import type { PropertyDetail } from '../api';
import { costLineText } from '../format';
import { rentalFigures } from '../rental/RentalFigures';
import '@/locales/properties';

/** Spec §5.2: the property today inline under the title, every figure the server's; an unknown one reads « Inconnue ». */
export function PropertyStats({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const f = property.figures;
  const m = (amount: string) => money(property.currency, amount);
  const unknown = t('property_detail.unknown');
  const value = f.value && f.valueOn
    ? t(f.stale ? 'property_detail.value_stale' : 'property_detail.value_on', { amount: m(f.value), date: formatLocalDate(f.valueOn) })
    : unknown;
  const rent = f.rentedSince ? rentalFigures(t, property) : null;
  return (
    <StatLine items={[
      { label: t('property_detail.value'), value },
      { label: t('property_detail.net_value'), value: f.netValue ? m(f.netValue) : unknown },
      { label: t('property_detail.latent_gain'), value: f.latentGain ? signedMoney(property.currency, f.latentGain) : unknown },
      { label: t('property_detail.real_cost'), value: m(f.realCost) },
      ...(rent ? [
        { label: t('property_rental.expected_rent'), value: rent.expected },
        { label: t('property_rental.vacancy'), value: rent.vacancy },
        { label: t('property_rental.net_yield'), value: rent.net },
      ] : []),
    ]} />
  );
}

/** The rest of today's figures and the real cost's lines in the server's order. */
export function PropertyCostDetail({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const f = property.figures;
  const m = (amount: string) => money(property.currency, amount);
  return (
    <div className="space-y-3">
      <Terms items={[
        [t('property_detail.cost_price'), m(f.costPrice)],
        [t('property_detail.linked_balance'), m(f.linkedBalance)],
        [t('property_detail.net_cost_of_rents'), m(f.netCostOfRents)],
      ]} />
      <h3 className="font-semibold">{t('property_detail.real_cost_lines')}</h3>
      <Terms items={f.realCostLines.map((l): [string, string] => [costLineText(t, l.code), m(l.amount)])} />
    </div>
  );
}
