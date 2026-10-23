import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/app/Providers';
import { formatMoney } from '@/components/Money';
import type { DashboardView } from '../api';
import { percent } from '../format';
import { Value } from '../KeyFigures';
import '@/locales/tax';

const SOURCE_KEYS = ['subscriptions', 'loans', 'property_costs', 'tax'] as const;

/** Spec §4.2: what the month's commitments are made of, as the server split them. */
export function CommitmentsPanel({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const has = (m: string) => capabilities.modules.includes(m);
  return (
    <div className="space-y-4">
      <div className="space-y-1">
        <Value figure={d.commitments} unknown={t(d.commitmentSources.length > 0 ? 'dashboard.commitments_none_this_month' : 'dashboard.commitments_none')} />
        <p className="text-sm text-muted-foreground">{t('dashboard.amortized_per_month')}</p>
        {d.committedPct != null ? <p className="text-sm text-muted-foreground">{t('dashboard.committed_share', { value: percent(d.committedPct) })}</p> : null}
        {d.cashThisMonth.total ? <p className="text-sm text-muted-foreground">{t('dashboard.cash_this_month', { amount: formatMoney(d.cashThisMonth.total) })}</p> : null}
      </div>
      <ul className="space-y-1">
        {d.commitmentSources.flatMap((s) => {
          const key = SOURCE_KEYS.find((k) => k === s.source);
          return key && s.amortized ? [<li key={key} className="tabular-nums">{t(`dashboard.commitment_source.${key}`, { amount: formatMoney(s.amortized) })}</li>] : [];
        })}
      </ul>
      <div className="flex flex-wrap gap-4 text-sm">
        {has('subscriptions') ? <Link to="/subscriptions" className="underline">{t('dashboard.see_subscriptions')}</Link> : null}
        {has('realestate') ? <Link to="/loans" className="underline">{t('dashboard.see_loans')}</Link> : null}
      </div>
    </div>
  );
}
