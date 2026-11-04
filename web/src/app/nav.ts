import { Baby, Bell, Building2, Calculator, CalendarCheck, FileText, FlaskConical, Download, House, Inbox, KeyRound, Landmark, PiggyBank, Repeat, ShieldCheck, Settings, Target, Upload, Wallet } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import type { Tile } from '@/components/ModuleTile';
import type { Capabilities } from './Providers';

/** The phone's tab order (the other entries sit in « Plus »). */
export const TAB_ORDER: NavItem['to'][] = ['/dashboard', '/investments', '/subscriptions', '/routine'];

/** screens.md §1: one list for the sidebar, the bottom tabs and the « Plus » page. */
export const NAV = [
  { group: 'follow', module: 'core', admin: false, mobile: 'tab', to: '/dashboard', icon: House, tile: 'primary', label: 'nav.dashboard' },
  { group: 'follow', module: ['investments', 'realestate'], admin: false, mobile: 'tab', to: '/routine', icon: CalendarCheck, tile: 'primary', label: 'nav.routine' },
  { group: 'follow', module: 'core', admin: false, mobile: 'more', to: '/proposals', icon: Inbox, tile: 'primary', label: 'nav.proposals' },
  { group: 'follow', module: 'documents', admin: false, mobile: 'more', to: '/documents', icon: FileText, tile: 'neutral', label: 'nav.documents' },
  { group: 'estate', module: 'investments', admin: false, mobile: 'tab', to: '/investments', icon: PiggyBank, tile: 1, label: 'nav.investments' },
  { group: 'estate', module: 'goals', admin: false, mobile: 'more', to: '/goals', icon: Target, tile: 4, label: 'nav.goals' },
  { group: 'estate', module: 'realestate', admin: false, mobile: 'more', to: '/properties', icon: Building2, tile: 3, label: 'nav.properties' },
  { group: 'estate', module: 'realestate', admin: false, mobile: 'more', to: '/loans', icon: Landmark, tile: 2, label: 'nav.loans' },
  { group: 'budget', module: 'income', admin: false, mobile: 'more', to: '/income', icon: Wallet, tile: 6, label: 'nav.income' },
  { group: 'budget', module: 'income', admin: false, mobile: 'more', to: '/tax', icon: Calculator, tile: 'neutral', label: 'nav.tax' },
  { group: 'estate', module: 'core', admin: false, mobile: 'more', to: '/scenarios', icon: FlaskConical, tile: 7, label: 'nav.scenarios' },
  { group: 'budget', module: 'subscriptions', admin: false, mobile: 'tab', to: '/subscriptions', icon: Repeat, tile: 8, label: 'nav.subscriptions' },
  { group: 'budget', module: 'childcare', admin: false, mobile: 'more', to: '/childcare', icon: Baby, tile: 5, label: 'nav.childcare' },
  { group: 'account', module: 'import', admin: false, mobile: 'more', to: '/import', icon: Upload, tile: 'neutral', label: 'nav.import' },
  { group: 'account', module: 'core', admin: false, mobile: 'more', to: '/export', icon: Download, tile: 'neutral', label: 'nav.export' },
  { group: 'account', module: null, admin: false, mobile: 'more', to: '/security', icon: KeyRound, tile: 'neutral', label: 'nav.security' },
  { group: 'account', module: null, admin: false, mobile: 'more', to: '/settings/appearance', icon: Settings, tile: 'neutral', label: 'nav.settings' },
  { group: 'account', module: null, admin: true, mobile: 'more', to: '/admin/users', icon: ShieldCheck, tile: 'neutral', label: 'nav.admin' },
] as const satisfies readonly ({ tile: Tile } & Record<string, unknown>)[];

export type NavItem = (typeof NAV)[number];

export function visibleNav(capabilities: Capabilities): NavItem[] {
  return NAV.filter((item) =>
    (item.module === null || [item.module].flat().some((m) => capabilities.modules.includes(m))) && (!item.admin || capabilities.admin));
}

/** The first path segment an entry stands for: « Plus » is current on any page under one of its entries. */
export const section = (to: string) => `/${to.split('/')[1]}`;

/** The entry of the module a path belongs to (its first segment), if any. */
export const moduleOf = (pathname: string): NavItem | undefined => NAV.find((item) => section(item.to) === section(pathname));

/** Pages outside the sidebar that still belong to a colour slot. */
export const PLACES = { '/notifications': { icon: Bell, tile: 'primary' } } as const;

export type Place = NavItem['to'] | keyof typeof PLACES;

/** The icon and colour slot of a module's page, for its tile. */
export function tileOf(to: Place): { icon: LucideIcon; tile: Tile } {
  const { icon, tile } = to in PLACES ? PLACES[to as keyof typeof PLACES] : NAV.find((i) => i.to === to)!;
  return { icon, tile };
}

/** The page of a module the server names (an event's `module`), if it has one. */
export function placeOf(module: string): Place | undefined {
  const to = `/${module}`;
  return NAV.some((i) => i.to === to) ? (to as Place) : undefined;
}
