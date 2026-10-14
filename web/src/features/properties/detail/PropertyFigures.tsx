import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { Terms } from '../../loans/detail/Terms';
import { money, signedMoney } from '../../loans/format';
import type { PropertyDetail } from '../api';
import { costLineText } from '../format';

/** Spec §6: the property today, every figure, total and line the server's; an unknown one reads « Inconnue ». */
export function PropertyFigures({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const f = property.figures;
  const m = (amount: string) => money(property.currency, amount);
  const unknown = t('property_detail.unknown');
  const value = f.value && f.valueOn
    ? t(f.stale ? 'property_detail.value_stale' : 'property_detail.value_on', { amount: m(f.value), date: formatLocalDate(f.valueOn) })
    : unknown;
  const items: [string, string][] = [
    [t('property_detail.value'), value],
    [t('property_detail.cost_price'), m(f.costPrice)],
    [t('property_detail.latent_gain'), f.latentGain ? signedMoney(property.currency, f.latentGain) : unknown],
    [t('property_detail.linked_balance'), m(f.linkedBalance)],
    [t('property_detail.net_value'), f.netValue ? m(f.netValue) : unknown],
    [t('property_detail.real_cost'), m(f.realCost)],
    [t('property_detail.net_cost_of_rents'), m(f.netCostOfRents)],
  ];
  return (
    <div className="space-y-3">
      <Terms items={items} />
      <h3 className="font-semibold">{t('property_detail.real_cost_lines')}</h3>
      <Terms items={f.realCostLines.map((l): [string, string] => [costLineText(t, l.code), m(l.amount)])} />
    </div>
  );
}
