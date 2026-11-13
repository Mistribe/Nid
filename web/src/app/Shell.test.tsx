import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { use } from 'react';
import { describe, expect, it, onTestFinished } from 'vitest';
import { dashboard, todoItem } from '@/features/dashboard/testData';
import { detail, overview, SALARY, sourceRow } from '@/features/income/testData';
import { detail as envelope, PEA } from '@/features/investments/testData';
import { expectNoAxeViolations } from '@/test/axe';
import { createAppRouter } from '@/app/router';
import { adminCapabilities, aliceSession, allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

// The menu is a lazy chunk: its real trigger (aria-expanded) replaces the plain initials once loaded.
const openerReady = () => screen.findByRole('button', { name: 'Alice Dev', expanded: false });
const sidebar = () => screen.getByRole('navigation', { name: 'Navigation principale' });
const tabs = () => screen.getByRole('navigation', { name: 'Onglets' });

describe('the shell', () => {
  it('lists the enabled modules and settings', async () => {
    await renderApp('/subscriptions');
    const links = within(sidebar()).getAllByRole('link').map((l) => l.textContent);
    expect(links).toEqual(['Accueil', 'Routine', 'À valider', 'Documents', 'Placements', 'Objectifs', 'Biens', 'Crédits', 'Retraite', 'Simulations', 'Revenus', 'Impôts', 'Abonnements', 'Garde']);
  });

  it('each sidebar entry shows its tile, also on the rail', async () => {
    const { user } = await renderApp('/subscriptions');
    const everyLinkHasOneTile = () => {
      for (const link of within(sidebar()).getAllByRole('link')) expect(link.querySelectorAll('[data-tile]'), link.textContent).toHaveLength(1);
    };
    everyLinkHasOneTile();
    expect(tabs().querySelector('[data-tile]')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Replier le menu' }));
    everyLinkHasOneTile();
    const link = within(sidebar()).getByRole('link', { name: 'Placements' });
    expect(link.querySelector('.sr-only')).toHaveTextContent('Placements');
    expect(link.querySelector('[data-tile] svg')).not.toHaveClass('text-primary');
  });

  it('the sidebar, the top bar and the phone tabs are floating glass panes', async () => {
    const { container } = await renderApp('/subscriptions');
    for (const pane of ['sidebar', 'toolbar', 'tabs']) {
      const el = container.querySelector(`[data-pane="${pane}"]`);
      expect(el, pane).toHaveClass('glass', 'rounded-pane');
    }
    expect(sidebar()).not.toHaveClass('border-r');
    expect(sidebar()).not.toHaveClass('bg-sidebar');
  });

  it('the routed page sits in one content window, never on the bare ground', async () => {
    await renderApp('/subscriptions');
    const pane = (await screen.findByRole('heading', { level: 1 })).closest('[data-pane="content"]');
    expect(pane).toHaveClass('content-pane', 'rounded-pane');
  });

  it('marks the current page with a tinted pill, its label in the text colour', async () => {
    await renderApp('/subscriptions');
    for (const nav of [sidebar(), tabs()]) {
      const current = within(nav).getByRole('link', { current: 'page' });
      expect(current).toHaveClass('text-foreground', 'font-semibold');
      expect(current).not.toHaveClass('text-primary');
    }
  });

  it('the top bar fades the content scrolling under it', async () => {
    const { container } = await renderApp('/subscriptions');
    expect(container.querySelector('header [data-fade]')).toHaveClass('scroll-edge');
    expect(container.querySelector('header')).not.toHaveClass('scroll-edge');
  });

  // The header's box runs the full width and 8 px below the pane: only the pane takes the pointer.
  it('the top bar’s box lets the pointer through to the content, its pane does not', async () => {
    const { container } = await renderApp('/subscriptions');
    expect(container.querySelector('header')).toHaveClass('pointer-events-none');
    expect(container.querySelector('[data-pane="toolbar"]')).toHaveClass('pointer-events-auto');
  });

  // A phone's 390 px: the trail keeps « Placements » whole between the tight row and the bar's 36 px icon buttons.
  it('the top bar packs tighter on a phone, its icon buttons 36 px', async () => {
    const { container } = await renderApp('/subscriptions');
    const row = container.querySelector<HTMLElement>('[data-pane="toolbar"] > div')!;
    expect(row).toHaveClass('gap-1', 'px-3', 'md:gap-4', 'md:px-4');
    expect(row).not.toHaveClass('gap-2', 'px-4');
    const trailing = row.lastElementChild!;
    expect(trailing).toHaveClass('gap-1', 'md:gap-2');
    expect(trailing).not.toHaveClass('gap-2');
    expect(within(row).getByRole('button', { name: 'Rechercher ou aller à…' })).toHaveClass('h-9');
  });

  // iOS's tab label size: « Abonnements » fits a fifth of 390 px, bold, without truncating.
  it('phone tab labels are 10 px, tight, on a near-edge padding', async () => {
    await renderApp('/subscriptions');
    for (const link of within(tabs()).getAllByRole('link')) expect(link).toHaveClass('text-[10px]', 'leading-tight', 'px-0.5');
  });

  it('lists « Garde » only with the childcare module', async () => {
    await renderApp('/subscriptions', { capabilities: { modules: ['core', 'subscriptions', 'income'], admin: false } });
    expect(within(sidebar()).queryByRole('link', { name: 'Garde' })).not.toBeInTheDocument();
  });

  it('lists « Objectifs » only with the goals module', async () => {
    await renderApp('/subscriptions', { capabilities: { modules: ['core', 'subscriptions', 'investments'], admin: false } });
    expect(within(sidebar()).queryByRole('link', { name: 'Objectifs' })).not.toBeInTheDocument();
  });

  it('groups the sidebar into Suivi, Patrimoine and Budget', async () => {
    await renderApp('/subscriptions');
    const nav = within(sidebar());
    expect(nav.getAllByRole('heading').map((h) => h.textContent)).toEqual(['Suivi', 'Patrimoine', 'Budget']);
    const groups = nav.getAllByRole('list').map((l) => within(l).getAllByRole('link').map((a) => a.textContent));
    expect(groups).toEqual([['Accueil', 'Routine', 'À valider', 'Documents'], ['Placements', 'Objectifs', 'Biens', 'Crédits', 'Retraite', 'Simulations'], ['Revenus', 'Impôts', 'Abonnements', 'Garde']]);
  });

  it('shows the API version beside the web version, in the person menu', async () => {
    server.use(http.get('*/api/v1/platform/version', () => HttpResponse.json({ version: '1.2.3' })));
    const { user } = await renderApp('/subscriptions');
    await user.click(await openerReady());
    // The line reads « API : … » while the version is still loading.
    await waitFor(() => expect(screen.getByRole('menuitem', { name: /^Version dev/ })).toHaveTextContent('API : 1.2.3'));
  });

  it('opens « Nouveautés » on a click on the version', async () => {
    server.use(http.get('*/changelog.json', () => HttpResponse.json([])));
    const { user, router } = await renderApp('/subscriptions');
    await user.click(await openerReady());
    await user.click(await screen.findByRole('menuitem', { name: /^Version dev/ }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Nouveautés' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/changelog');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('puts account pages in the person menu', async () => {
    const { user } = await renderApp('/subscriptions');
    await user.click(await openerReady());
    const menu = await screen.findByRole('menu');
    expect(within(menu).getAllByRole('menuitem').map((i) => i.textContent)).toEqual(['Import', 'Export', 'Sécurité', 'Réglages', 'Se déconnecter', 'Version devAPI : …']);
    // The name heads the menu.
    expect(menu.firstElementChild).toHaveTextContent('Alice Dev');
  });

  // Issue 374: the menu sat at the sidebar's foot, a bare initials disc on the rail and nowhere on a phone.
  it('ends the top bar with the person menu, the sidebar with its toggle only', async () => {
    const { user, container } = await renderApp('/subscriptions');
    const row = container.querySelector<HTMLElement>('[data-pane="toolbar"] > div')!;
    const opener = await openerReady();
    expect(opener).toHaveTextContent('AD');
    expect(row.lastElementChild!.lastElementChild).toBe(opener);
    expect(within(sidebar()).queryByRole('button', { name: 'Alice Dev' })).not.toBeInTheDocument();
    expect(within(sidebar()).queryByText(/^Version/)).not.toBeInTheDocument();
    opener.focus();
    await user.keyboard('{Enter}');
    expect(await screen.findByRole('menu')).toBeInTheDocument();
  });

  it('has no Administration entry in the person menu', async () => {
    const { user } = await renderApp('/subscriptions', { capabilities: adminCapabilities });
    await user.click(await openerReady());
    await screen.findByRole('menu');
    expect(screen.queryByRole('menuitem', { name: 'Administration' })).not.toBeInTheDocument();
  });

  it('shows the Administration group in the sidebar for an admin', async () => {
    await renderApp('/admin', { capabilities: adminCapabilities });
    const links = within(within(sidebar()).getByRole('list', { name: 'Administration' })).getAllByRole('link').map((l) => l.textContent);
    expect(links).toEqual(['Vue d\'ensemble', 'Taux de change', 'Cours', 'IRL', 'Inflation', 'Tâches', 'Banque', 'Catalogue', 'Utilisateurs', 'Foyer', 'Journal de sécurité', 'Système', 'Notifications']);
  });

  describe('the Administration group', () => {
    const heading = () => within(sidebar()).getByRole('button', { name: 'Administration' });

    it('is folded by default and folds with a click or the keyboard, remembered on this device', async () => {
      const { user, container } = await renderApp('/subscriptions', { capabilities: adminCapabilities });
      expect(heading()).toHaveAttribute('aria-expanded', 'false');
      expect(within(sidebar()).queryByRole('link', { name: 'Taux de change' })).not.toBeInTheDocument();
      await expectNoAxeViolations(container);
      await user.click(heading());
      expect(heading()).toHaveAttribute('aria-expanded', 'true');
      expect(document.getElementById(heading().getAttribute('aria-controls')!)).toBe(within(sidebar()).getByRole('list', { name: 'Administration' }));
      expect(within(sidebar()).getByRole('link', { name: 'Taux de change' })).toBeInTheDocument();
      expect(localStorage.getItem('nid.sidebar.admin')).toBe('open');
      heading().focus();
      await user.keyboard('{Enter}');
      expect(heading()).toHaveAttribute('aria-expanded', 'false');
      await user.keyboard(' ');
      expect(heading()).toHaveAttribute('aria-expanded', 'true');
    });

    it('opens as it was left', async () => {
      localStorage.setItem('nid.sidebar.admin', 'open');
      await renderApp('/subscriptions', { capabilities: adminCapabilities });
      expect(heading()).toHaveAttribute('aria-expanded', 'true');
    });

    it('unfolds on landing on an admin page, even when left folded', async () => {
      localStorage.setItem('nid.sidebar.admin', 'closed');
      const { router } = await renderApp('/subscriptions', { capabilities: adminCapabilities });
      expect(heading()).toHaveAttribute('aria-expanded', 'false');
      await router.navigate({ to: '/admin/fx' });
      await waitFor(() => expect(heading()).toHaveAttribute('aria-expanded', 'true'));
      expect(within(sidebar()).getByRole('link', { name: 'Taux de change' })).toHaveAttribute('aria-current', 'page');
    });

    it('is not foldable on the rail', async () => {
      localStorage.setItem('nid.sidebar', 'rail');
      await renderApp('/subscriptions', { capabilities: adminCapabilities });
      expect(within(sidebar()).queryByRole('button', { name: 'Administration' })).not.toBeInTheDocument();
    });
  });

  it('shows no Administration group to a non-admin', async () => {
    await renderApp('/subscriptions', { capabilities: allCapabilities });
    expect(within(sidebar()).queryByRole('heading', { name: 'Administration' })).not.toBeInTheDocument();
  });

  it('marks the admin section current in the sidebar', async () => {
    await renderApp('/admin/fx', { capabilities: adminCapabilities });
    expect(within(sidebar()).getByRole('link', { name: 'Taux de change' })).toHaveAttribute('aria-current', 'page');
    expect(within(sidebar()).getByRole('link', { name: 'Vue d\'ensemble' })).not.toHaveAttribute('aria-current');
  });

  it('keeps Tâches current on a job page and marks Vue d\'ensemble on /admin', async () => {
    await renderApp('/admin/jobs/fx-daily', { capabilities: adminCapabilities });
    expect(await within(sidebar()).findByRole('link', { name: 'Tâches' })).toHaveAttribute('aria-current', 'page');
  });

  it('marks Vue d\'ensemble current on /admin', async () => {
    await renderApp('/admin', { capabilities: adminCapabilities });
    expect(await within(sidebar()).findByRole('link', { name: 'Vue d\'ensemble' })).toHaveAttribute('aria-current', 'page');
  });

  it('shows the routine badge with the dashboard’s todo count', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard({ todo: [todoItem(), todoItem(), todoItem()] }))));
    await renderApp('/subscriptions');
    await waitFor(() => expect(within(sidebar()).getByRole('link', { name: /Routine/ })).toHaveTextContent('3'));
  });

  it('counts only stale and missing values in the routine badge, not income gaps', async () => {
    const gap = todoItem({ kind: 'income_gap_soon', module: 'income', name: 'Salaire' });
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard({ todo: [todoItem(), gap] }))));
    await renderApp('/subscriptions');
    await waitFor(() => expect(within(sidebar()).getByRole('link', { name: /Routine/ })).toHaveTextContent('1'));
  });

  it('shows no routine badge for a household with only an income gap', async () => {
    const gap = todoItem({ kind: 'income_gap_soon', module: 'income', name: 'Salaire' });
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard({ todo: [gap] }))));
    const { queryClient } = await renderApp('/subscriptions');
    await waitFor(() => expect(queryClient.getQueryCache().findAll().some((q) => q.state.status === 'success' && String(q.queryKey[1]).includes('dashboard'))).toBe(true));
    expect(within(sidebar()).getByRole('link', { name: 'Routine' })).toHaveTextContent(/^Routine$/);
  });

  it('shows no routine badge when the dashboard fails', async () => {
    server.use(http.get('*/api/v1/dashboard', () => new HttpResponse(null, { status: 403 })));
    const { queryClient } = await renderApp('/subscriptions');
    // The query has taken the 500 into its state: a badge could no longer appear for lack of waiting.
    await waitFor(() => expect(queryClient.getQueryCache().findAll().some((q) => q.state.status === 'error' && String(q.queryKey[1]).includes('dashboard'))).toBe(true));
    expect(within(sidebar()).getByRole('link', { name: 'Routine' })).toBeInTheDocument();
    expect(within(sidebar()).getByRole('link', { name: 'Routine' })).toHaveTextContent(/^Routine$/);
  });

  it('offers Export even where import is disabled', async () => {
    const { user } = await renderApp('/subscriptions', { capabilities: { modules: ['core', 'subscriptions'], admin: false } });
    await user.click(await openerReady());
    expect(await screen.findByRole('menuitem', { name: 'Export' })).toBeInTheDocument();
  });

  it('hides a module the server does not enable, and its route sends you home', async () => {
    const { router } = await renderApp('/import', { capabilities: { modules: ['core', 'subscriptions'], admin: false } });
    expect(within(sidebar()).queryByRole('link', { name: 'Import' })).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/dashboard');
  });

  it('offers « Crédits » only with the realestate module, its routes sending you home otherwise', async () => {
    const { router } = await renderApp('/loans', { capabilities: { modules: ['core', 'subscriptions', 'investments', 'income'], admin: false } });
    expect(within(sidebar()).queryByRole('link', { name: 'Crédits' })).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/dashboard');
  });

  it('keeps « Crédits » out of the phone’s tabs: it sits in « Plus »', async () => {
    await renderApp('/loans');
    expect(within(tabs()).queryByRole('link', { name: 'Crédits' })).not.toBeInTheDocument();
    expect(within(sidebar()).getByRole('link', { name: 'Crédits' })).toHaveAttribute('href', '/loans');
  });

  it('offers « Biens » only with the realestate module, its routes sending you home otherwise', async () => {
    const { router } = await renderApp('/properties', { capabilities: { modules: ['core', 'subscriptions', 'investments', 'income'], admin: false } });
    expect(within(sidebar()).queryByRole('link', { name: 'Biens' })).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/dashboard');
  });

  it('keeps « Biens » out of the phone’s tabs: it sits in « Plus », just before « Crédits »', async () => {
    await renderApp('/properties');
    expect(within(tabs()).queryByRole('link', { name: 'Biens' })).not.toBeInTheDocument();
    expect(within(sidebar()).getByRole('link', { name: 'Biens' })).toHaveAttribute('href', '/properties');
  });

  it('opens on the dashboard', async () => {
    const { router } = await renderApp('/');
    expect(router.state.location.pathname).toBe('/dashboard');
  });

  it('keeps Moi through a link to a detail and through the breadcrumb', async () => {
    server.use(
      http.get('*/api/v1/income/overview', () => HttpResponse.json(overview({ sources: [sourceRow()] }))),
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
    );
    const { router, user } = await renderApp('/income?scope=me&tab=sources');

    await user.click((await screen.findAllByRole('link', { name: /Salaire Alice/ }))[0]!);
    expect(router.state.location.pathname).toBe(`/income/${SALARY}`);
    expect(router.state.location.search).toEqual({ scope: 'me' });

    await screen.findByRole('heading', { level: 1, name: 'Salaire Alice' });
    await user.click(within(await screen.findByRole('navigation', { name: 'Fil d’Ariane' })).getByRole('link', { name: 'Revenus' }));
    expect(router.state.location.pathname).toBe('/income');
    expect(router.state.location.search).toEqual({ scope: 'me' });
  });

  it('the selector back to Foyer removes the scope', async () => {
    const { router, user } = await renderApp('/subscriptions?scope=me');
    await user.click(screen.getByRole('radio', { name: 'Foyer' }));
    expect(router.state.location.search).toEqual({});
    await user.click(within(sidebar()).getByRole('link', { name: 'Placements' }));
    expect(router.state.location.search).toEqual({});
  });

  it('keeps Foyer / Moi in the URL across navigation', async () => {
    const { router, user } = await renderApp('/subscriptions');
    expect(await screen.findByRole('radio', { name: 'Foyer' })).toBeChecked();

    await user.click(screen.getByRole('radio', { name: 'Moi' }));
    expect(router.state.location.search).toEqual({ scope: 'me' });

    await user.click(within(sidebar()).getByRole('link', { name: 'Placements' }));
    expect(router.state.location.pathname).toBe('/investments');
    expect(router.state.location.search).toEqual({ scope: 'me' });
    expect(screen.getByRole('radio', { name: 'Moi' })).toBeChecked();
  });

  it('hides Foyer / Moi while I own no private item, since both would show the same', async () => {
    let answered = false;
    server.use(http.get('*/api/v1/core/me/private-items', () => { answered = true; return HttpResponse.json({ owns: false }); }));
    await renderApp('/subscriptions');
    await waitFor(() => expect(answered).toBe(true));
    expect(screen.queryByRole('radio', { name: 'Foyer' })).not.toBeInTheDocument();
  });

  it('keeps the selector on a Moi link even without a private item, to leave a way back', async () => {
    server.use(http.get('*/api/v1/core/me/private-items', () => HttpResponse.json({ owns: false })));
    await renderApp('/subscriptions?scope=me');
    expect(screen.getByRole('radio', { name: 'Moi' })).toBeChecked();
  });

  it('shows the not-found page for an unknown path', async () => {
    await renderApp('/nowhere');
    const heading = await screen.findByRole('heading', { level: 1, name: 'Page introuvable' });
    expect(heading.closest('[data-pane="content"]')).toHaveClass('content-pane');
    // Issue 362: built like an empty state, the way home offered; the trail never reads the error sentence.
    expect(heading.closest('section')!.querySelector('[data-tile]')).not.toBeNull();
    expect(within(heading.closest('section')!).getByRole('link', { name: 'Accueil' })).toHaveAttribute('href', '/');
    expect(screen.queryByRole('navigation', { name: 'Fil d’Ariane' })?.textContent ?? '').not.toContain('introuvable');
  });

  it('shows a module’s not-found page under its tile, with a way to its list', async () => {
    await renderApp('/investments/a/b');
    const heading = await screen.findByRole('heading', { level: 1, name: 'Page introuvable' });
    const page = heading.closest('section')!;
    expect(page.querySelector('[data-tile="1"]')).not.toBeNull();
    expect(within(page).getByRole('link', { name: 'Ouvrir Placements' })).toHaveAttribute('href', '/investments');
  });

  it('signs out with the CSRF token and says what stays open', async () => {
    let csrf: string | null = null;
    server.use(http.post('*/auth/logout', ({ request }) => {
      csrf = request.headers.get('X-Nid-Csrf');
      return new HttpResponse(null, { status: 204 });
    }));
    const { location, user } = await renderApp('/subscriptions');

    await user.click(await openerReady());
    await user.click(await screen.findByRole('menuitem', { name: 'Se déconnecter' }));

    expect(csrf).toBe('csrf-alice');
    expect(location.assign).toHaveBeenCalledWith('/signed-out');
  });

  it('stays when the sign-out is refused', async () => {
    server.use(http.post('*/auth/logout', () => new HttpResponse(null, { status: 403 })));
    const { location, user } = await renderApp('/subscriptions');

    await user.click(await openerReady());
    await user.click(await screen.findByRole('menuitem', { name: 'Se déconnecter' }));

    expect(location.assign).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Alice Dev' })).toBeInTheDocument();
  });

  it('signs out from the phone’s Plus page', async () => {
    let csrf: string | null = null;
    server.use(http.post('*/auth/logout', ({ request }) => {
      csrf = request.headers.get('X-Nid-Csrf');
      return new HttpResponse(null, { status: 204 });
    }));
    const { location, user } = await renderApp('/more');

    await user.click(await screen.findByRole('button', { name: 'Se déconnecter' }));

    await waitFor(() => expect(location.assign).toHaveBeenCalledWith('/signed-out'));
    expect(csrf).toBe('csrf-alice');
  });

  it('has no accessibility violation', async () => {
    const { container } = await renderApp('/subscriptions');
    await expectNoAxeViolations(container);
  });
  it('phone tabs are Accueil, Placements, Abonnements, Routine, Plus', async () => {
    await renderApp('/subscriptions');
    expect(within(tabs()).getAllByRole('link').map((l) => l.textContent)).toEqual(['Accueil', 'Placements', 'Abonnements', 'Routine', 'Plus']);
  });

  it('the tab bar follows the enabled modules', async () => {
    await renderApp('/subscriptions', { capabilities: { modules: ['core', 'subscriptions', 'investments'], admin: false } });
    expect(within(tabs()).getAllByRole('link').map((l) => l.textContent)).toEqual(['Accueil', 'Placements', 'Abonnements', 'Routine', 'Plus']);
  });

  it('lists the other sections on /more, Administration for an admin only', async () => {
    const { container } = await renderApp('/more');
    const list = await screen.findByRole('list', { name: 'Autres sections' });
    expect(within(list).getAllByRole('link').map((l) => l.textContent)).toEqual(['À valider', 'Documents', 'Objectifs', 'Biens', 'Crédits', 'Revenus', 'Impôts', 'Retraite', 'Simulations', 'Garde', 'Import', 'Export', 'Sécurité', 'Réglages', 'Nouveautés']);
    await expectNoAxeViolations(container);
  });

  it('shows Administration on /more for an admin', async () => {
    await renderApp('/more', { capabilities: adminCapabilities });
    const list = await screen.findByRole('list', { name: 'Autres sections' });
    expect(within(list).getByRole('link', { name: 'Administration' })).toBeInTheDocument();
  });

  it('keeps the admin sections off /more', async () => {
    await renderApp('/more', { capabilities: adminCapabilities });
    const list = await screen.findByRole('list', { name: 'Autres sections' });
    expect(within(list).getAllByRole('link', { name: 'Administration' })).toHaveLength(1);
    expect(within(list).queryByRole('link', { name: 'Taux de change' })).not.toBeInTheDocument();
  });

  it('marks Plus current on a page it gathers', async () => {
    await renderApp('/import');
    expect(within(tabs()).getByRole('link', { name: 'Plus' })).toHaveAttribute('aria-current', 'page');
    expect(within(tabs()).getByRole('link', { name: 'Accueil' })).not.toHaveAttribute('aria-current');
  });

  it('marks Plus current below one of its entries', async () => {
    await renderApp('/admin/fx', { capabilities: adminCapabilities });
    expect(within(tabs()).getByRole('link', { name: 'Plus' })).toHaveAttribute('aria-current', 'page');
  });

  it('marks Plus current on /more', async () => {
    await renderApp('/more');
    expect(within(tabs()).getByRole('link', { name: 'Plus' })).toHaveAttribute('aria-current', 'page');
  });

  it('keeps every entry in the sidebar and no Plus', async () => {
    await renderApp('/subscriptions');
    expect(within(sidebar()).queryByRole('link', { name: 'Plus' })).not.toBeInTheDocument();
  });
  describe('the rail', () => {
    const toggle = (name: string) => screen.getByRole('button', { name });

    it('the sidebar shows the wordmark, the rail only the mark', async () => {
      const { user } = await renderApp('/subscriptions');
      const brand = () => sidebar().querySelector('.h-14')!;
      expect(brand().querySelector('[data-wordmark]')).not.toBeNull();
      expect(sidebar().querySelector('svg.lucide-bird')).toBeNull();
      await user.click(toggle('Replier le menu'));
      expect(brand().querySelector('[data-wordmark]')).toBeNull();
      expect(brand().querySelector('ellipse')).not.toBeNull();
    });

    it('keeps an admin link named on the rail', async () => {
      localStorage.setItem('nid.sidebar', 'rail');
      await renderApp('/subscriptions', { capabilities: adminCapabilities });
      expect(within(sidebar()).getByRole('link', { name: 'Taux de change' })).toBeInTheDocument();
    });

    it('folds the sidebar into a rail and remembers it on this device', async () => {
      const { user } = await renderApp('/subscriptions');
      await user.click(toggle('Replier le menu'));
      expect(sidebar()).toHaveAttribute('data-rail');
      expect(localStorage.getItem('nid.sidebar')).toBe('rail');
      // The names stay for screen readers, and the groups keep their headings.
      expect(within(sidebar()).getAllByRole('link').map((l) => l.textContent)).toEqual(['Accueil', 'Routine', 'À valider', 'Documents', 'Placements', 'Objectifs', 'Biens', 'Crédits', 'Retraite', 'Simulations', 'Revenus', 'Impôts', 'Abonnements', 'Garde']);
      expect(within(sidebar()).getAllByRole('heading').map((h) => h.textContent)).toEqual(['Suivi', 'Patrimoine', 'Budget']);
      await user.click(toggle('Déplier le menu'));
      expect(sidebar()).not.toHaveAttribute('data-rail');
      expect(localStorage.getItem('nid.sidebar')).toBe('full');
    });

    // Issue 373: in the top bar's centred row it drifted away from the rail on a wide screen.
    it('keeps its toggle at the sidebar foot, in both states', async () => {
      const { user, container } = await renderApp('/subscriptions');
      const toolbar = container.querySelector<HTMLElement>('[data-pane="toolbar"]')!;
      expect(within(toolbar).queryByRole('button', { name: 'Replier le menu' })).not.toBeInTheDocument();
      const button = within(sidebar()).getByRole('button', { name: 'Replier le menu' });
      expect(sidebar().querySelector('[data-pane="sidebar"]')!.lastElementChild).toContainElement(button);
      await user.click(button);
      expect(sidebar().querySelector('[data-pane="sidebar"]')!.lastElementChild).toContainElement(within(sidebar()).getByRole('button', { name: 'Déplier le menu' }));
    });

    it('toggles with Ctrl B', async () => {
      const { user } = await renderApp('/subscriptions');
      await user.keyboard('{Control>}b{/Control}');
      expect(sidebar()).toHaveAttribute('data-rail');
      await user.keyboard('{Control>}b{/Control}');
      expect(sidebar()).not.toHaveAttribute('data-rail');
    });

    it('opens as a rail when it was left so', async () => {
      localStorage.setItem('nid.sidebar', 'rail');
      await renderApp('/subscriptions');
      expect(sidebar()).toHaveAttribute('data-rail');
      expect(await screen.findByRole('button', { name: 'Alice Dev', expanded: false })).toHaveTextContent('AD');
    });

    it('names an entry beside its icon, with its count', async () => {
      server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard({ todo: [todoItem(), todoItem()] }))));
      localStorage.setItem('nid.sidebar', 'rail');
      const { user } = await renderApp('/subscriptions');
      // The tooltip loads lazily and replaces the bare link it falls back to: query the link again, once wrapped.
      const routine = () => within(sidebar()).getByRole('link', { name: /Routine/ });
      await waitFor(() => expect(routine()).toHaveTextContent('2 à faire'));
      await waitFor(() => expect(routine()).toHaveAttribute('data-state'));
      await user.hover(routine());
      expect(await screen.findByRole('tooltip')).toHaveTextContent('Routine2');
    });

    it('has no accessibility violation', async () => {
      localStorage.setItem('nid.sidebar', 'rail');
      const { container } = await renderApp('/subscriptions');
      await expectNoAxeViolations(container);
    });
  });
});

describe('the top bar trail', () => {
  const bar = () => document.querySelector<HTMLElement>('[data-pane="toolbar"]')!;
  const trail = () => within(bar()).getByRole('navigation', { name: 'Fil d’Ariane' });
  const serveEnvelope = () => server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(envelope())));

  it('shows the page trail, ending with the current page', async () => {
    serveEnvelope();
    await renderApp(`/investments/${PEA}`);
    await screen.findByRole('heading', { level: 1, name: 'PEA' });
    expect(within(trail()).getByRole('link', { name: 'Placements' })).toHaveAttribute('href', '/investments');
    const last = within(trail()).getAllByRole('listitem').filter((li) => li.textContent).at(-1)!;
    expect(last).toHaveTextContent('PEA');
    expect(within(last).getByText('PEA')).toHaveAttribute('aria-current', 'page');
    expect(document.querySelector('header[data-page-header]')!.querySelector('nav[aria-label="Fil d’Ariane"]')).toBeNull();
    expect(screen.getAllByRole('navigation', { name: 'Fil d’Ariane' })).toHaveLength(1);
  });

  it('a page with no crumbs gets its module from the navigation', async () => {
    await renderApp('/settings/appearance');
    await screen.findByRole('heading', { level: 1, name: 'Réglages' });
    // The module is the page's own section: plain text, not a link to here.
    expect(within(trail()).getByText('Réglages')).not.toHaveAttribute('aria-current');
    expect(within(trail()).queryByRole('link')).toBeNull();
    expect(within(trail()).getByText('Apparence')).toHaveAttribute('aria-current', 'page');
    expect(within(bar()).queryByRole('link', { name: /^Retour/ })).toBeNull();
  });

  it('a top-level page shows its name alone', async () => {
    await renderApp('/investments');
    await screen.findByRole('heading', { level: 1, name: 'Placements' });
    expect(within(trail()).queryByRole('link')).toBeNull();
    expect(within(trail()).getByText('Placements')).toHaveAttribute('aria-current', 'page');
  });

  it('while a page suspends, the bar shows the module of its route', async () => {
    // A page that suspends for good registers no trail of its own. The route tree is shared by the worker's files
    // (isolate: false) and its lazy component keeps the first module it loaded, so a vi.mock of the page would leak
    // into the page's own tests, or miss once they loaded it: swap the route's component for this test only.
    const route = createAppRouter({ capabilities: allCapabilities }).routesById['/childcare/$id/statements/$month'];
    const component = route.options.component;
    const never = new Promise<never>(() => {});
    route.options.component = function Suspended() { return use(never); };
    onTestFinished(() => { route.options.component = component; });
    await renderApp('/childcare/0192f0aa-0000-7000-8000-00000000c001/statements/2025-09');
    await waitFor(() => expect(within(trail()).getByText('Garde')).toHaveAttribute('aria-current', 'page'));
    expect(within(trail()).queryByRole('link', { name: 'Garde' })).toBeNull();
  });

  it('moving to another page replaces the trail', async () => {
    serveEnvelope();
    const { user } = await renderApp(`/investments/${PEA}`);
    await screen.findByRole('heading', { level: 1, name: 'PEA' });
    await user.click(within(trail()).getByRole('link', { name: 'Placements' }));
    await screen.findByRole('heading', { level: 1, name: 'Placements' });
    expect(within(trail()).getByText('Placements')).toHaveAttribute('aria-current', 'page');
    expect(trail()).not.toHaveTextContent('PEA');
  });

  it('long names truncate in the bar', async () => {
    serveEnvelope();
    await renderApp(`/investments/${PEA}`);
    await screen.findByRole('heading', { level: 1, name: 'PEA' });
    expect(within(trail()).getByRole('list')).toHaveClass('min-w-0');
    for (const text of [within(trail()).getByRole('link', { name: 'Placements' }), within(trail()).getByText('PEA')]) {
      expect(text).toHaveClass('truncate');
      expect(text.className).toMatch(/max-w-/);
    }
  });

  it('on a phone the bar offers « ‹ Parent », and the wordmark on a top-level page', async () => {
    serveEnvelope();
    await renderApp(`/investments/${PEA}`);
    await screen.findByRole('heading', { level: 1, name: 'PEA' });
    const back = within(bar()).getByRole('link', { name: 'Retour à Placements' });
    expect(back).toHaveAttribute('href', '/investments');
    expect(back).toHaveClass('md:hidden');
    expect(back).not.toHaveAttribute('aria-current');
    expect(within(trail()).getByRole('link', { name: 'Placements' })).not.toHaveAttribute('aria-current');
    expect(within(trail()).queryByRole('link', { name: 'PEA' })).toBeNull();
    expect(bar().querySelector('[data-wordmark]')).toBeNull();
  });

  it('keeps the ancestors while the current title absorbs the squeeze', async () => {
    serveEnvelope();
    await renderApp(`/investments/${PEA}`);
    await screen.findByRole('heading', { level: 1, name: 'PEA' });
    expect(within(trail()).getByRole('link', { name: 'Placements' }).closest('li')).toHaveClass('shrink-[0.1]');
    expect(within(trail()).getByText('PEA').closest('li')).not.toHaveClass('shrink-[0.1]');
  });

  it('the phone back link has a focus ring and a touch target', async () => {
    serveEnvelope();
    await renderApp(`/investments/${PEA}`);
    await screen.findByRole('heading', { level: 1, name: 'PEA' });
    expect(within(bar()).getByRole('link', { name: 'Retour à Placements' })).toHaveClass('h-9', 'pointer-coarse:h-11', 'focus-visible:ring-[3px]');
  });

  it('the end group never shrinks, so the scope switch stays on one line', async () => {
    await renderApp('/investments');
    await screen.findByRole('heading', { level: 1, name: 'Placements' });
    expect(bar().querySelector('.ml-auto')).toHaveClass('shrink-0');
  });

  it('a top-level page has the wordmark and no back link on a phone', async () => {
    await renderApp('/investments');
    await screen.findByRole('heading', { level: 1, name: 'Placements' });
    expect(bar().querySelector('[data-wordmark]')).not.toBeNull();
    expect(within(bar()).queryByRole('link', { name: /^Retour/ })).toBeNull();
  });
});

describe('the demo banner', () => {
  it('says on every screen that the data is invented, in demo mode only', async () => {
    await renderApp('/subscriptions', { session: { ...aliceSession, demo: true } });
    expect(screen.getByRole('note')).toHaveTextContent('Démo : les données sont inventées et réinitialisées chaque nuit.');
    expect(screen.getByRole('link', { name: 'Voir Nid sur GitHub' })).toHaveAttribute('href', 'https://github.com/Mistribe/Nid');
  });

  it('shows nothing outside demo mode', async () => {
    await renderApp('/subscriptions');
    expect(screen.queryByText(/les données sont inventées/)).toBeNull();
  });
});
