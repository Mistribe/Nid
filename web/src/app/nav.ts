import { Baby, Building2, Calculator, CalendarCheck, FileText, FlaskConical, Download, House, Inbox, KeyRound, Landmark, PiggyBank, Repeat, ShieldCheck, Settings, Target, Upload, Wallet } from 'lucide-react';
import type { Capabilities } from './Providers';

/** The phone's tab order (the other entries sit in « Plus »). */
export const TAB_ORDER: NavItem['to'][] = ['/dashboard', '/investments', '/subscriptions', '/routine'];

/** screens.md §1: one list for the sidebar, the bottom tabs and the « Plus » page. */
export const NAV = [
  { group: 'follow', module: 'core', admin: false, mobile: 'tab', to: '/dashboard', icon: House, label: 'nav.dashboard' },
  { group: 'follow', module: ['investments', 'realestate'], admin: false, mobile: 'tab', to: '/routine', icon: CalendarCheck, label: 'nav.routine' },
  { group: 'follow', module: 'core', admin: false, mobile: 'more', to: '/proposals', icon: Inbox, label: 'nav.proposals' },
  { group: 'follow', module: 'documents', admin: false, mobile: 'more', to: '/documents', icon: FileText, label: 'nav.documents' },
  { group: 'estate', module: 'investments', admin: false, mobile: 'tab', to: '/investments', icon: PiggyBank, label: 'nav.investments' },
  { group: 'estate', module: 'goals', admin: false, mobile: 'more', to: '/goals', icon: Target, label: 'nav.goals' },
  { group: 'estate', module: 'realestate', admin: false, mobile: 'more', to: '/properties', icon: Building2, label: 'nav.properties' },
  { group: 'estate', module: 'realestate', admin: false, mobile: 'more', to: '/loans', icon: Landmark, label: 'nav.loans' },
  { group: 'budget', module: 'income', admin: false, mobile: 'more', to: '/income', icon: Wallet, label: 'nav.income' },
  { group: 'budget', module: 'income', admin: false, mobile: 'more', to: '/tax', icon: Calculator, label: 'nav.tax' },
  { group: 'estate', module: 'core', admin: false, mobile: 'more', to: '/scenarios', icon: FlaskConical, label: 'nav.scenarios' },
  { group: 'budget', module: 'subscriptions', admin: false, mobile: 'tab', to: '/subscriptions', icon: Repeat, label: 'nav.subscriptions' },
  { group: 'budget', module: 'childcare', admin: false, mobile: 'more', to: '/childcare', icon: Baby, label: 'nav.childcare' },
  { group: 'account', module: 'import', admin: false, mobile: 'more', to: '/import', icon: Upload, label: 'nav.import' },
  { group: 'account', module: 'core', admin: false, mobile: 'more', to: '/export', icon: Download, label: 'nav.export' },
  { group: 'account', module: null, admin: false, mobile: 'more', to: '/security', icon: KeyRound, label: 'nav.security' },
  { group: 'account', module: null, admin: false, mobile: 'more', to: '/settings/appearance', icon: Settings, label: 'nav.settings' },
  { group: 'account', module: null, admin: true, mobile: 'more', to: '/admin/users', icon: ShieldCheck, label: 'nav.admin' },
] as const;

export type NavItem = (typeof NAV)[number];

export function visibleNav(capabilities: Capabilities): NavItem[] {
  return NAV.filter((item) =>
    (item.module === null || [item.module].flat().some((m) => capabilities.modules.includes(m))) && (!item.admin || capabilities.admin));
}

/** The first path segment an entry stands for: « Plus » is current on any page under one of its entries. */
export const section = (to: string) => `/${to.split('/')[1]}`;
