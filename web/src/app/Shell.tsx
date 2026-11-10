import { lazy, Suspense, useEffect, useState, type CSSProperties } from 'react';
import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { Ellipsis, PanelLeftClose, PanelLeftOpen, Search } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ModuleTile } from '@/components/ModuleTile';
import { Wordmark } from '@/components/Logo';
import { ScopeSwitch } from '@/components/ScopeSwitch';
import { Guides } from '@/features/guides/Guides';
import { PendingBadge } from '@/features/proposals/PendingBadge';
import { TokenBanner } from '@/features/security/TokenBanner';
import { NotificationBell } from './NotificationBell';
import { RoutineBadge } from './RoutineBadge';
import { TopBarTrail } from './TopBarTrail';
import { TrailProvider } from '@/components/page/trail';
import { section, TAB_ORDER, visibleNav, type NavItem } from '@/components/nav';
import { useSession } from './Providers';
import { AVATAR, initials, useSidebar } from './sidebar';

// The menu's Radix code stays out of the initial bundle (200 kB budget); the initials show meanwhile.
const PersonMenu = lazy(() => import('./PersonMenu').then((m) => ({ default: m.PersonMenu })));
// The rail's tooltips load with the first rail (200 kB budget); the bare link shows meanwhile.
const RailTip = lazy(() => import('./RailTip').then((m) => ({ default: m.RailTip })));
// cmdk and the dialog load on the first ⌘K (200 kB budget).
// sonner loads after the shell (200 kB budget); a toast fired before it mounts is replayed when it does.
const Toaster = lazy(() => import('@/components/ui/sonner').then((m) => ({ default: m.Toaster })));
const CommandMenu = lazy(() => import('./CommandMenu').then((m) => ({ default: m.CommandMenu })));
const MAC = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform);

// Sized for a mouse; a finger (coarse pointer, a tablet) keeps 44 px.
const LINK = 'relative flex min-h-9 items-center gap-2.5 rounded-md px-3 py-1.5 text-sm transition-colors hover:bg-accent hover:text-accent-foreground pointer-coarse:min-h-11';
// On the rail the entry is its icon, centred.
const RAIL = 'relative flex h-10 items-center justify-center rounded-md transition-colors hover:bg-accent hover:text-accent-foreground pointer-coarse:h-11';
// The current page: a pill tinted in the accent colour, the label in the text colour and bold (design-system.md §8.1), the icon in the accent.
const ACTIVE = 'bg-primary/15 font-semibold text-foreground hover:bg-primary/20 hover:text-foreground [&>svg]:text-primary';
// A tab colours its icon, not its label.
const TAB_ACTIVE = 'font-semibold text-foreground [&>svg]:text-primary';
// A phone shares its width among five tabs: icon above a short label, each tab an equal share (2E1 final review). iOS's 10 px
// labels on a 2 px edge: « Abonnements » in bold fits a fifth of 390 px whole.
const TAB = 'relative flex min-h-11 min-w-0 flex-1 flex-col items-center justify-center gap-0.5 px-0.5 py-1 text-[10px] leading-tight hover:bg-accent hover:text-accent-foreground';

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
  const inMore = pathname === '/more' || pathname === '/changelog'
    || more.some((item) => pathname === section(item.to) || pathname.startsWith(`${section(item.to)}/`));

  const sideLink = (item: NavItem) => {
    const anchor = (
      <Link to={item.to} className={rail ? RAIL : LINK} activeProps={{ 'aria-current': 'page', className: ACTIVE }}>
        <ModuleTile icon={item.icon} tile={item.tile} />
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
    <Link key={item.to} to={item.to} className={TAB} activeProps={{ 'aria-current': 'page', className: TAB_ACTIVE }}>
      <item.icon aria-hidden="true" className="size-5 shrink-0" />
      <span className="max-w-full truncate">{t(item.label)}</span>
      {/* On a tab the count sits on the icon's corner, out of the label's way. */}
      <span className="absolute top-0.5 left-1/2 ml-1.5">{badge(item)}</span>
    </Link>
  );

  const toggleLabel = t(rail ? 'nav.expand' : 'nav.collapse');
  const toggleShortcut = t(MAC ? 'nav.toggle_shortcut_mac' : 'nav.toggle_shortcut');

  return (
    <TrailProvider>
    <div
      style={{ '--sidebar-width': rail ? '4rem' : '14rem' } as CSSProperties}
      className="min-h-dvh md:grid md:grid-cols-[var(--sidebar-width)_minmax(0,1fr)] md:transition-[grid-template-columns] md:duration-200 md:ease-emphasized motion-reduce:transition-none print:block">
      <nav id="sidebar" aria-label={t('nav.sidebar')} data-rail={rail || undefined}
        className="hidden print:hidden md:sticky md:top-0 md:block md:h-dvh md:p-2">
        <div data-pane="sidebar" className="glass rounded-pane flex h-full flex-col overflow-hidden text-sidebar-foreground">
          {/* As tall as the top bar's row, so the brand and the search line up. */}
          <div className={`flex h-14 shrink-0 items-center ${rail ? 'justify-center' : 'px-5'}`}>
            <Wordmark markOnly={rail} className="h-7" />
          </div>
          <div className={`flex-1 overflow-x-hidden overflow-y-auto ${rail ? 'p-1' : 'p-3'}`}>
            {groups.map(({ group, entries }, index) => (
              <div key={group} className={index === 0 ? undefined : rail ? 'mt-2 border-t pt-2' : 'mt-5'}>
                <h2 className={rail ? 'sr-only' : 'px-3 pb-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground'}>{t(`nav.group_${group}`)}</h2>
                <ul className="space-y-0.5">{entries.map((item) => <li key={item.to}>{sideLink(item)}</li>)}</ul>
              </div>
            ))}
          </div>
          {/* Bottom left in both states, 14 px from the window's edge: folding or unfolding leaves it under the pointer (issue 373). */}
          <div className="shrink-0 border-t p-1.5">
            <button type="button" onClick={toggle} aria-label={toggleLabel} aria-keyshortcuts={MAC ? 'Meta+B' : 'Control+B'} title={`${toggleLabel} (${toggleShortcut})`}
              className="flex size-9 items-center justify-center rounded-md text-muted-foreground transition-colors outline-none hover:bg-accent hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50">
              {rail ? <PanelLeftOpen aria-hidden="true" className="size-4.5" /> : <PanelLeftClose aria-hidden="true" className="size-4.5" />}
            </button>
          </div>
        </div>
      </nav>
      <div className="flex min-h-dvh min-w-0 flex-col pb-[calc(5rem+env(safe-area-inset-bottom))] md:pb-0">
        {/* Sticky over the page it scrolls; the search opens the ⌘K palette, the scope and the guides sit at the end. Its box
            runs the full width and 8 px below the pane: only the pane takes the pointer. */}
        <header className="pointer-events-none sticky top-0 z-30 px-2 pt-[calc(env(safe-area-inset-top)+0.5rem)] pb-2 md:pl-0 print:hidden">
          {/* The fade sits behind the pane: a mask or backdrop-filter on an ancestor would cut the pane's own blur. */}
          <div aria-hidden="true" data-fade className="scroll-edge pointer-events-none absolute inset-0 -z-10" />
          <div data-pane="toolbar" className="glass rounded-pane pointer-events-auto">
            <div className="mx-auto flex h-14 w-full max-w-7xl items-center gap-1 px-3 max-[22rem]:gap-0.5 max-[22rem]:px-1.5 md:gap-4 md:px-4">
              <TopBarTrail />
              <button type="button" onClick={() => setPalette(true)} aria-label={t('command.open')} aria-keyshortcuts={MAC ? 'Meta+K' : 'Control+K'}
                className="flex h-9 items-center gap-2 rounded-md border bg-card px-2.5 text-sm text-muted-foreground shadow-xs transition-colors outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 md:w-full md:min-w-9 md:max-w-sm md:overflow-hidden md:shrink-[10]">
                <Search aria-hidden="true" className="size-4 shrink-0" />
                <span aria-hidden="true" className="hidden flex-1 truncate text-left md:inline">{t('command.open')}</span>
                <kbd aria-hidden="true" className="hidden rounded border bg-muted px-1.5 font-sans text-xs whitespace-nowrap xl:inline">{t(MAC ? 'command.shortcut_mac' : 'command.shortcut')}</kbd>
              </button>
              {/* A 320 px phone: the person menu and « Foyer / Moi » fit on the row with 2 px between the icons (issue 374). */}
              <div className="ml-auto flex shrink-0 items-center gap-1 max-[22rem]:gap-0.5 md:gap-2">
                <NotificationBell /><ScopeSwitch /><Guides />
                <Suspense fallback={<p className="flex size-9 items-center justify-center"><span aria-hidden="true" className={AVATAR}>{initials(session.person.name)}</span><span className="sr-only">{session.person.name}</span></p>}>
                  <PersonMenu />
                </Suspense>
              </div>
            </div>
          </div>
        </header>
        {palette !== null ? <Suspense fallback={null}><CommandMenu open={palette} onOpenChange={setPalette} /></Suspense> : null}
        <TokenBanner />
        {/* The page is one frosted window, like the panes around it: text never sits on the bare halo. */}
        <main className="flex flex-1 flex-col px-2 pb-2 md:pl-0">
          <div data-pane="content" className="content-pane rounded-pane flex-1 p-4 shadow-card md:p-6">
            {/* A line of reading and charts that stay legible on a wide screen: past 1 280 px the page centres. */}
            <div className="mx-auto w-full max-w-7xl"><Outlet /></div>
          </div>
        </main>
      </div>
      <nav aria-label={t('nav.tabs')} data-pane="tabs" className="glass rounded-pane fixed inset-x-2 bottom-[calc(env(safe-area-inset-bottom)+0.5rem)] z-30 flex md:hidden print:hidden">
        {items.filter((item) => item.mobile === 'tab').sort((a, b) => TAB_ORDER.indexOf(a.to) - TAB_ORDER.indexOf(b.to)).map(tab)}
        <Link to="/more" aria-current={inMore ? 'page' : undefined} className={inMore ? `${TAB} ${TAB_ACTIVE}` : TAB}>
          <Ellipsis aria-hidden="true" className="size-5 shrink-0" />
          <span className="max-w-full truncate">{t('nav.more')}</span>
          {more.some((item) => item.to === '/proposals') ? <span className="absolute top-0.5 left-1/2 ml-1.5"><PendingBadge /></span> : null}
        </Link>
      </nav>
      {/* An action's outcome (design-system.md §5); on a phone, above the tab bar. */}
      <Suspense fallback={null}>
        <Toaster containerAriaLabel={t('app.toasts')} position="bottom-right"
          mobileOffset={{ bottom: 'calc(5rem + env(safe-area-inset-bottom) + 1rem)' }} />
      </Suspense>
    </div>
    </TrailProvider>
  );
}
