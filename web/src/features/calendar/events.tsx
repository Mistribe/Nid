import type { LinkProps } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { Building2, CalendarClock, CalendarX, FileText, Flag, Gift, Hourglass, KeyRound, Landmark, Milestone, Percent, PiggyBank, RefreshCw, Receipt, Scale, TimerOff, TrendingUp, Wallet, type LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { DashboardEvent } from '@/api/dashboard';
import { itemRoute } from '@/components/ItemLink';
import { categoryText } from '../properties/format';

/** A dated event of « À venir » (the dashboard) and of the calendar page (#239): what happens, by its kind. */
export function EventText({ e }: { e: DashboardEvent }) {
  const { t } = useTranslation();
  switch (e.kind) {
    case 'subscription_renewal': return <>{t('dashboard.upcoming.subscription_renewal')}</>;
    case 'subscription_expiry': return <>{t('dashboard.upcoming.subscription_expiry')}</>;
    case 'subscription_cancel_deadline': return <>{t('dashboard.upcoming.subscription_cancel_deadline')}</>;
    case 'subscription_trial_end': return <>{t('dashboard.upcoming.subscription_trial_end')}</>;
    case 'subscription_price_increase': return <>{t('dashboard.upcoming.subscription_price_increase')}</>;
    case 'income_phase_end': return <>{t('dashboard.upcoming.income_phase_end', { label: e.label ?? '' })}</>;
    case 'loan_phase_end': return <>{t('dashboard.upcoming.loan_phase_end', { n: e.label ?? '' })}</>;
    case 'loan_end': return <>{t('dashboard.upcoming.loan_end')}</>;
    case 'valuation_due': return <>{t('dashboard.upcoming.valuation_due')}</>;
    case 'property_cost_due': return <>{categoryText(t, e.label)}</>;
    case 'tenancy_end': return <>{t('dashboard.upcoming.tenancy_end', { label: e.label ?? '' })}</>;
    case 'holding_maturity': return <>{t('dashboard.upcoming.holding_maturity', { label: e.label ?? '' })}</>;
    case 'holding_entitlement': return <>{t('dashboard.upcoming.holding_entitlement', { label: e.label ?? '' })}</>;
    case 'tax_milestone': {
      const milestone = (['life_insurance_8y', 'pea_5y', 'per_retirement'] as const).find((m) => m === e.label);
      return milestone ? <>{t(`dashboard.upcoming.tax_milestone.${milestone}`)}</> : null;
    }
    case 'per_deadline': return <>{t('dashboard.upcoming.per_deadline')}</>;
    case 'per_ceiling_expiring': return <>{t('dashboard.upcoming.per_ceiling_expiring')}</>;
    case 'tax_filing_deadline': return <>{t('dashboard.upcoming.tax_filing_deadline')}</>;
    case 'tax_new_rate': return <>{t('dashboard.upcoming.tax_new_rate')}</>;
    case 'tax_balance_due': return <>{t('dashboard.upcoming.tax_balance_due')}</>;
    case 'property_tax_due': return <>{t('dashboard.upcoming.property_tax_due')}</>;
    default: return null;
  }
}

const ICON: Record<string, LucideIcon> = {
  subscription_renewal: RefreshCw, subscription_expiry: CalendarX, subscription_cancel_deadline: CalendarClock,
  subscription_trial_end: Gift, subscription_price_increase: TrendingUp, income_phase_end: Wallet, loan_phase_end: Landmark,
  loan_end: Flag, valuation_due: Scale, property_cost_due: Receipt, tenancy_end: KeyRound, holding_maturity: Hourglass, holding_entitlement: Building2,
  per_deadline: PiggyBank, per_ceiling_expiring: TimerOff, tax_milestone: Milestone,
  tax_filing_deadline: FileText, tax_new_rate: Percent, tax_balance_due: Landmark, property_tax_due: Receipt,
};

export const eventIcon = (e: DashboardEvent): LucideIcon => ICON[e.kind] ?? CalendarClock;

/** The PER deadline and the tax calendar are about a person or the household, not an item: they lead to the tax page. */
export const eventLink = (e: DashboardEvent): LinkProps => (e.id ? itemRoute(e.module, e.id) : { to: '/tax' });

/** Whom or what the event is about: its item's name, else the household or the PER holder. */
export const eventDetail = (t: TFunction, e: DashboardEvent): string =>
  e.id ? e.name : e.label === 'household' ? t('dashboard.upcoming.per_household')
    : e.label === 'department_unknown' ? t('dashboard.upcoming.tax_department_unknown') : e.name;
