import { Download, House, KeyRound, PiggyBank, Repeat, ShieldCheck, Settings, Upload, Wallet } from 'lucide-react';
import type { Capabilities } from './Providers';

/** screens.md §1: one list for the sidebar, the bottom tabs and the « Plus » page. */
export const NAV = [
  { module: 'core', admin: false, mobile: 'tab', to: '/dashboard', icon: House, label: 'nav.dashboard' },
  { module: 'subscriptions', admin: false, mobile: 'tab', to: '/subscriptions', icon: Repeat, label: 'nav.subscriptions' },
  { module: 'investments', admin: false, mobile: 'tab', to: '/investments', icon: PiggyBank, label: 'nav.investments' },
  { module: 'income', admin: false, mobile: 'tab', to: '/income', icon: Wallet, label: 'nav.income' },
  { module: 'import', admin: false, mobile: 'more', to: '/import', icon: Upload, label: 'nav.import' },
  { module: 'core', admin: false, mobile: 'more', to: '/export', icon: Download, label: 'nav.export' },
  { module: null, admin: false, mobile: 'more', to: '/security', icon: KeyRound, label: 'nav.security' },
  { module: null, admin: false, mobile: 'more', to: '/settings/appearance', icon: Settings, label: 'nav.settings' },
  { module: null, admin: true, mobile: 'more', to: '/admin/users', icon: ShieldCheck, label: 'nav.admin' },
] as const;

export type NavItem = (typeof NAV)[number];

export function visibleNav(capabilities: Capabilities): NavItem[] {
  return NAV.filter((item) =>
    (item.module === null || capabilities.modules.includes(item.module)) && (!item.admin || capabilities.admin));
}

/** The first path segment an entry stands for: « Plus » is current on any page under one of its entries. */
export const section = (to: string) => `/${to.split('/')[1]}`;
