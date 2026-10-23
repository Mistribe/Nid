import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import type { DashboardView } from '../api';
import { HouseholdMonth } from '../HouseholdMonth';

/** Spec §4.2: the margin and the month it comes from, repeated from the home. */
export function MarginPanel({ d, scope }: { d: DashboardView; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-4">
      {d.margin ? (
        <div className="space-y-1">
          <span className="text-2xl font-semibold tracking-tight">{formatMoney(d.margin)}</span>
          <p className="text-sm text-muted-foreground">{t('dashboard.key.margin_hint')}</p>
        </div>
      ) : null}
      <HouseholdMonth d={d} scope={scope} compact />
    </div>
  );
}
