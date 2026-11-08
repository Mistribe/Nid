import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { StatLine } from '@/components/page/StatLine';
import { monthLabel } from '@/lib/format';
import { Terms } from '../../loans/detail/Terms';
import { money, percent, signedMoney } from '../../loans/format';
import type { PropertyDetail } from '@/api/properties';
import { costLineText } from '../format';
import { rentalFigures } from '../rental/RentalFigures';
import '@/locales/properties';

/** Spec §5.2: the property today inline under the title, every figure the server's; an unknown one reads « Inconnue ». */
export function PropertyStats({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const f = property.figures;
  const m = (amount: string) => money(property.currency, amount);
  const unknown = t('property_detail.unknown');
  // #376: the figure alone on its line, its date (and « À réestimer ») in the line under it.
  const value = f.value && f.valueOn
    ? { value: m(f.value), hint: t(f.stale ? 'property_detail.value_stale' : 'property_detail.value_on', { date: formatLocalDate(f.valueOn) }) }
    : { value: unknown };
  const rent = f.rentedSince ? rentalFigures(t, property) : null;
  return (
    <StatLine items={[
      { label: t('property_detail.value'), ...value },
      { label: t('property_detail.net_value'), value: f.netValue ? <>{m(f.netValue)}<NetValueTurn property={property} /></> : unknown },
      { label: t('property_detail.latent_gain'), value: f.latentGain ? signedMoney(property.currency, f.latentGain) : unknown },
      { label: t('property_detail.real_cost'), value: m(f.realCost) },
      ...(rent ? [
        { label: t('property_rental.expected_rent'), value: rent.expected },
        f.vacantDays != null && f.vacancyRate
          ? { label: t('property_rental.vacancy'), value: percent(t, f.vacancyRate), hint: t('properties_rental.vacancy_days', { count: f.vacantDays }) }
          : { label: t('property_rental.vacancy'), value: unknown },
        f.netYieldBeforeTax
          ? { label: t('property_rental.net_yield'), value: rent.net }
          : { label: t('property_rental.net_yield'), value: t('properties_rental.not_significant'), hint: t('properties_rental.not_significant_note') },
      ] : []),
    ]} />
  );
}

/** Issue #406: since when a negative net value is so, and the month the server found it turns positive, the value held flat. */
function NetValueTurn({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const turn = property.figures.netValueTurn;
  if (!turn) return null;
  const [year, month] = (turn.positiveOn ?? '').split('-').map(Number);
  return (
    <span className="block text-sm font-normal text-muted-foreground">
      {t('property_detail.net_negative_since', { date: formatLocalDate(turn.negativeSince) })}
      {' · '}
      {turn.positiveOn ? t('property_detail.net_positive_on', { month: monthLabel(year ?? 0, month ?? 1) }) : t('property_detail.net_positive_beyond')}
    </span>
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
