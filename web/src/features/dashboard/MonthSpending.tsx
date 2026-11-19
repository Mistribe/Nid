import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { StatLine } from '@/components/page/StatLine';
import type { DashboardView } from '@/api/dashboard';

type Money = NonNullable<DashboardView['margin']>;

/** #85 decision 10: the margin, the everyday spending it pays for and what is really left; loaded lazily with its InfoTip. */
export function MonthSpending({ margin, everyday, left }: { margin: Money; everyday: Money; left: Money }) {
  const { t } = useTranslation();
  return (
    <StatLine items={[
      { label: t('dashboard.key.margin'), value: formatMoney(margin), hint: t('dashboard.key.margin_hint') },
      { label: t('dashboard.month.everyday'), value: formatMoney(everyday), tip: t('dashboard.month.everyday_tip') },
      {
        label: t('dashboard.month.left'),
        // An overspend reads as such, its amount the server's without the sign (a string cut, no arithmetic).
        value: left.amount.startsWith('-')
          ? t('dashboard.month.overspent', { amount: formatMoney({ ...left, amount: left.amount.slice(1) }) })
          : formatMoney(left),
      },
    ]} />
  );
}
