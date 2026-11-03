import { lazy, Suspense, useEffect, useState, type CSSProperties } from 'react';
import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { Bird, Ellipsis, PanelLeftClose, PanelLeftOpen, Search } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ScopeSwitch } from '@/components/ScopeSwitch';
import { Guides } from '@/features/guides/Guides';
import { PendingBadge } from '@/features/proposals/PendingBadge';
import { TokenBanner } from '@/features/security/TokenBanner';
import { CriticalBanner } from './CriticalBanner';
import { NotificationBell } from './NotificationBell';
import { RoutineBadge } from './RoutineBadge';
import { section, TAB_ORDER, visibleNav, type NavItem } from './nav';
import { useSession } from './Providers';
import { initials, useSidebar } from './sidebar';

// The menu's Radix code stays out of the initial bundle (200 kB budget); the name shows meanwhile.
const PersonMenu = lazy(() => import('./PersonMenu').then((m) => ({ default: m.PersonMenu })));
const VersionInfo = lazy(() => import('./VersionInfo').then((m) => ({ default: m.VersionInfo })));
// The rail's tooltips load with the first rail (200 kB budget); the bare link shows meanwhile.
const RailTip = lazy(() => import('./RailTip').then((m) => ({ default: m.RailTip })));
const VERSION = import.meta.env.VITE_NID_VERSION ?? 'dev';
// cmdk and the dialog load on the first ⌘K (200 kB budget).
// sonner loads after the shell (200 kB budget); a toast fired before it mounts is replayed when it does.
const Toaster = lazy(() => import('@/components/ui/sonner').then((m) => ({ default: m.Toaster })));
const CommandMenu = lazy(() => import('./CommandMenu').then((m) => ({ default: m.CommandMenu })));
const MAC = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform);

// Sized for a mouse; a finger (coarse pointer, a tablet) keeps 44 px.
const LINK = 'relative flex min-h-9 items-center gap-2.5 rounded-md px-3 py-1.5 text-sm transition-colors hover:bg-accent hover:text-accent-foreground pointer-coarse:min-h-11';
// On the rail the entry is its icon, centred.
const RAIL = 'relative flex h-10 items-center justify-center rounded-md transition-colors hover:bg-accent hover:text-accent-foreground pointer-coarse:h-11';
// The current page: a tinted pill in the accent colour, the text bold so the colour is never alone.
const ACTIVE = 'bg-primary/10 font-semibold text-primary hover:bg-primary/15 hover:text-primary';
// A phone shares its width among five tabs: icon above a short label, each tab an equal share (2E1 final review).
const TAB = 'relative flex min-h-11 min-w-0 flex-1 flex-col items-center justify-center gap-0.5 px-1 py-1 text-xs hover:bg-accent hover:text-accent-foreground';

const badge = (item: NavItem, dot = false) =>
  item.to === '/proposals' ? <PendingBadge dot={dot} /> : item.to === '/routine' ? <RoutineBadge dot={dot} /> : null;

/** Sidebar on desktop, full or as a rail; on mobile, four tabs and « Plus » (screens.md §1). */
export function Shell() {
  const { t } = useTranslation();
  const { capabilities, session } = useSession();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const { rail, toggle } = useSidebar();
  // null until first opened: the palette's code is not fetched before it is wanted.
  const [palette, setPalette] = useState<boolean | null>(null);
  useEffect(() => {
    const shortcut = (event: KeyboardEvent) => {
      if (!(event.metaKey || event.ctrlKey)) return;
      const key = event.key.toLowerCase();
      if (key === 'k') {
        event.preventDefault();
        setPalette((open) => !open);
      } else if (key === 'b') {
        event.preventDefault();
        toggle();
      }
    };
    window.addEventListener('keydown', shortcut);
    return () => window.removeEventListener('keydown', shortcut);
  }, [toggle]);
  const items = visibleNav(capabilities);
  const side = items.filter((item) => item.group !== 'account');
  const groups = (['follow', 'estate', 'budget'] as const).map((group) => ({ group, entries: side.filter((item) => item.group === group) })).filter((g) => g.entries.length > 0);
  const more = items.filter((item) => item.mobile === 'more');
  const inMore = pathname === '/more'
    || more.some((item) => pathname === section(item.to) || pathname.startsWith(`${section(item.to)}/`));

  const sideLink = (item: NavItem) => {
    const anchor = (
      <Link to={item.to} className={rail ? RAIL : LINK} activeProps={{ 'aria-current': 'page', className: ACTIVE }}>
        <item.icon aria-hidden="true" className="size-4.5 shrink-0" />
        <span className={rail ? 'sr-only' : 'truncate'}>{t(item.label)}</span>
        {badge(item, rail)}
      </Link>
    );
    if (!rail) return anchor;
    return (
      <Suspense fallback={anchor}>
        <RailTip content={<>{t(item.label)}{badge(item)}</>}>{anchor}</RailTip>
      </Suspense>
    );
  };

  const tab = (item: NavItem) => (
    <Link key={item.to} to={item.to} className={TAB} activeProps={{ 'aria-current': 'page', className: 'font-semibold text-primary' }}>
      <item.icon aria-hidden="true" className="size-5 shrink-0" />
      <span className="max-w-full truncate">{t(item.label)}</span>
      {/* On a tab the count sits on the icon's corner, out of the label's way. */}
      <span className="absolute top-0.5 left-1/2 ml-1.5">{badge(item)}</span>
    </Link>
  );

  const toggleLabel = t(rail ? 'nav.expand' : 'nav.collapse');
  const toggleShortcut = t(MAC ? 'nav.toggle_shortcut_mac' : 'nav.toggle_shortcut');

  return (
    <div
      style={{ '--sidebar-width': rail ? '4rem' : '14rem' } as CSSProperties}
      className="min-h-dvh md:grid md:grid-cols-[var(--sidebar-width)_minmax(0,1fr)] md:transition-[grid-template-columns] md:duration-200 md:ease-emphasized motion-reduce:transition-none print:block">
      <nav id="sidebar" aria-label={t('nav.sidebar')} data-rail={rail || undefined}
        className="hidden overflow-hidden border-r bg-sidebar text-sidebar-foreground print:hidden md:sticky md:top-0 md:flex md:h-dvh md:flex-col">
        {/* As tall as the top bar, so their bottom borders make one line. */}
        <p className={`flex h-14 shrink-0 items-center gap-2 border-b font-semibold ${rail ? 'justify-center' : 'px-5 text-lg'}`}>
          <span aria-hidden="true" className="flex size-7 shrink-0 items-center justify-center rounded-lg bg-primary text-primary-foreground shadow-card"><Bird className="size-4" /></span>
          <span className={rail ? 'sr-only' : undefined}>{t('app.name')}</span>
        </p>
        <div className="flex-1 overflow-x-hidden overflow-y-auto p-3">
          {groups.map(({ group, entries }, index) => (
            <div key={group} className={index === 0 ? undefined : rail ? 'mt-2 border-t pt-2' : 'mt-5'}>
              <h2 className={rail ? 'sr-only' : 'px-3 pb-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground'}>{t(`nav.group_${group}`)}</h2>
              <ul className="space-y-0.5">{entries.map((item) => <li key={item.to}>{sideLink(item)}</li>)}</ul>
            </div>
          ))}
        </div>
        <div className="shrink-0 border-t p-3">
          <Suspense fallback={rail
            ? <p className="mx-auto flex size-10 items-center justify-center"><span aria-hidden="true" className="flex size-8 items-center justify-center rounded-full bg-primary/10 text-xs font-semibold text-primary">{initials(session.person.name)}</span><span className="sr-only">{session.person.name}</span></p>
            : <p className="truncate px-3 py-2 text-sm font-medium">{session.person.name}</p>}>
            <PersonMenu rail={rail} />
          </Suspense>
          {rail ? null : (
            <Suspense fallback={<p className="px-3 pt-1 text-xs text-muted-foreground">{t('app.version', { version: VERSION })}</p>}>
              <VersionInfo version={VERSION} />
            </Suspense>
          )}
        </div>
      </nav>
      <div className="flex min-h-dvh min-w-0 flex-col pb-[calc(4rem+env(safe-area-inset-bottom))] md:pb-0">
        {/* Sticky over the page it scrolls; the search opens the ⌘K palette, the scope and the guides sit at the end. */}
        <header className="sticky top-0 z-30 border-b bg-background/85 pt-[env(safe-area-inset-top)] backdrop-blur print:hidden">
          <div className="mx-auto flex h-14 w-full max-w-7xl items-center gap-2 px-4 md:gap-4">
            <button type="button" onClick={toggle} aria-label={toggleLabel} aria-controls="sidebar" aria-keyshortcuts={MAC ? 'Meta+B' : 'Control+B'} title={`${toggleLabel} (${toggleShortcut})`}
              className="hidden size-9 shrink-0 items-center justify-center rounded-md text-muted-foreground transition-colors outline-none hover:bg-accent hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 md:-ml-2 md:inline-flex">
              {rail ? <PanelLeftOpen aria-hidden="true" className="size-4.5" /> : <PanelLeftClose aria-hidden="true" className="size-4.5" />}
            </button>
            <p className="font-semibold md:hidden">{t('app.name')}</p>
            <button type="button" onClick={() => setPalette(true)} aria-label={t('command.open')} aria-keyshortcuts={MAC ? 'Meta+K' : 'Control+K'}
              className="flex h-9 items-center gap-2 rounded-md border bg-card px-2.5 text-sm text-muted-foreground shadow-xs transition-colors outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 md:w-full md:max-w-sm">
              <Search aria-hidden="true" className="size-4 shrink-0" />
              <span aria-hidden="true" className="hidden flex-1 text-left md:inline">{t('command.open')}</span>
              <kbd aria-hidden="true" className="hidden rounded border bg-muted px-1.5 font-sans text-xs md:inline">{t(MAC ? 'command.shortcut_mac' : 'command.shortcut')}</kbd>
            </button>
            <div className="ml-auto flex items-center gap-2"><NotificationBell /><ScopeSwitch /><Guides /></div>
          </div>
        </header>
        {palette !== null ? <Suspense fallback={null}><CommandMenu open={palette} onOpenChange={setPalette} /></Suspense> : null}
        <TokenBanner />
        <CriticalBanner />
        <main className="flex-1 p-4">
          {/* A line of reading and charts that stay legible on a wide screen: past 1 280 px the page centres. */}
          <div className="mx-auto w-full max-w-7xl"><Outlet /></div>
        </main>
      </div>
      <nav aria-label={t('nav.tabs')} className="fixed inset-x-0 bottom-0 z-30 flex border-t bg-background/90 pb-[env(safe-area-inset-bottom)] backdrop-blur md:hidden print:hidden">
        {items.filter((item) => item.mobile === 'tab').sort((a, b) => TAB_ORDER.indexOf(a.to) - TAB_ORDER.indexOf(b.to)).map(tab)}
        <Link to="/more" aria-current={inMore ? 'page' : undefined} className={inMore ? `${TAB} font-semibold text-primary` : TAB}>
          <Ellipsis aria-hidden="true" className="size-5 shrink-0" />
          <span className="max-w-full truncate">{t('nav.more')}</span>
          {more.some((item) => item.to === '/proposals') ? <span className="absolute top-0.5 left-1/2 ml-1.5"><PendingBadge /></span> : null}
        </Link>
      </nav>
      {/* An action's outcome (design-system.md §5); on a phone, above the tab bar. */}
      <Suspense fallback={null}>
        <Toaster containerAriaLabel={t('app.toasts')} position="bottom-right"
          mobileOffset={{ bottom: 'calc(4rem + env(safe-area-inset-bottom) + 1rem)' }} />
      </Suspense>
    </div>
  );
}
