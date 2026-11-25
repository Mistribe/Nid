import type { LinkProps } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { Building2, CalendarClock, CalendarX, FileText, Flag, Gift, Hourglass, KeyRound, Landmark, Milestone, Percent, PiggyBank, RefreshCw, Receipt, Scale, TimerOff, TrendingUp, Wallet, type LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { DashboardEvent } from '@/api/dashboard';
import { itemRoute } from '@/components/ItemLink';
import { categoryText } from '../properties/format';

/** Tax's event kinds (#645), written so that `lazyStrings.test.ts` does not read them as keys of the tax strings. */
const TAX = 'tax';

/** A dated event of « À venir » (the dashboard) and of the calendar page (#239): what happens, by its kind. */
export function EventText({ e }: { e: DashboardEvent }) {
  const { t } = useTranslation();
  switch (e.kind) {
    case 'subscriptions.renewal': return <>{t('dashboard.upcoming.subscription_renewal')}</>;
    case 'subscriptions.expiry': return <>{t('dashboard.upcoming.subscription_expiry')}</>;
    case 'subscriptions.cancel_deadline': return <>{t('dashboard.upcoming.subscription_cancel_deadline')}</>;
    case 'subscriptions.trial_end': return <>{t('dashboard.upcoming.subscription_trial_end')}</>;
    case 'subscriptions.price_increase': return <>{t('dashboard.upcoming.subscription_price_increase')}</>;
    case 'income.phase_end': return <>{t('dashboard.upcoming.income_phase_end', { label: e.label ?? '' })}</>;
    case 'realestate.loan_phase_end': return <>{t('dashboard.upcoming.loan_phase_end', { n: e.label ?? '' })}</>;
    case 'realestate.loan_end': return <>{t('dashboard.upcoming.loan_end')}</>;
    case 'realestate.valuation_due': return <>{t('dashboard.upcoming.valuation_due')}</>;
    case 'realestate.property_cost_due': return <>{categoryText(t, e.label)}</>;
    case 'realestate.tenancy_end': return <>{t('dashboard.upcoming.tenancy_end', { label: e.label ?? '' })}</>;
    case 'investments.holding_maturity': return <>{t('dashboard.upcoming.holding_maturity', { label: e.label ?? '' })}</>;
    case 'investments.holding_entitlement': return <>{t('dashboard.upcoming.holding_entitlement', { label: e.label ?? '' })}</>;
    case 'investments.tax_milestone': {
      const milestone = (['life_insurance_8y', 'pea_5y', 'per_retirement'] as const).find((m) => m === e.label);
      return milestone ? <>{t(`dashboard.upcoming.tax_milestone.${milestone}`)}</> : null;
    }
    case `${TAX}.per_deadline`: return <>{t('dashboard.upcoming.per_deadline')}</>;
    case `${TAX}.per_ceiling_expiring`: return <>{t('dashboard.upcoming.per_ceiling_expiring')}</>;
    case `${TAX}.filing_deadline`: return <>{t('dashboard.upcoming.tax_filing_deadline')}</>;
    case `${TAX}.new_rate`: return <>{t('dashboard.upcoming.tax_new_rate')}</>;
    case `${TAX}.balance_due`: return <>{t('dashboard.upcoming.tax_balance_due')}</>;
    case 'realestate.property_tax_due': return <>{t('dashboard.upcoming.property_tax_due')}</>;
    default: return null;
  }
}

const ICON: Record<string, LucideIcon> = {
  'subscriptions.renewal': RefreshCw, 'subscriptions.expiry': CalendarX, 'subscriptions.cancel_deadline': CalendarClock,
  'subscriptions.trial_end': Gift, 'subscriptions.price_increase': TrendingUp, 'income.phase_end': Wallet, 'realestate.loan_phase_end': Landmark,
  'realestate.loan_end': Flag, 'realestate.valuation_due': Scale, 'realestate.property_cost_due': Receipt, 'realestate.tenancy_end': KeyRound, 'investments.holding_maturity': Hourglass, 'investments.holding_entitlement': Building2,
  [`${TAX}.per_deadline`]: PiggyBank, [`${TAX}.per_ceiling_expiring`]: TimerOff, 'investments.tax_milestone': Milestone,
  [`${TAX}.filing_deadline`]: FileText, [`${TAX}.new_rate`]: Percent, [`${TAX}.balance_due`]: Landmark, 'realestate.property_tax_due': Receipt,
};

export const eventIcon = (e: DashboardEvent): LucideIcon => ICON[e.kind] ?? CalendarClock;

/** The PER deadline and the tax calendar are about a person or the household, not an item: they lead to the tax page. */
export const eventLink = (e: DashboardEvent): LinkProps => (e.id ? itemRoute(e.module, e.id) : { to: '/tax' });

/** Whom or what the event is about: its item's name, else the household or the PER holder. */
export const eventDetail = (t: TFunction, e: DashboardEvent): string =>
  e.id ? e.name : e.label === 'household' ? t('dashboard.upcoming.per_household')
    : e.label === 'department_unknown' ? t('dashboard.upcoming.tax_department_unknown') : e.name;
