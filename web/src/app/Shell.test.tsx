import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { dashboard, todoItem } from '@/features/dashboard/testData';
import { detail, overview, SALARY, sourceRow } from '@/features/income/testData';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

// The menu is a lazy chunk: its real trigger (aria-expanded) replaces the plain name once loaded.
const openerReady = () => screen.findByRole('button', { name: 'Alice Dev', expanded: false });
const sidebar = () => screen.getByRole('navigation', { name: 'Navigation principale' });
const tabs = () => screen.getByRole('navigation', { name: 'Onglets' });

describe('the shell', () => {
  it('lists the enabled modules and settings', async () => {
    await renderApp('/subscriptions');
    const links = within(sidebar()).getAllByRole('link').map((l) => l.textContent);
    expect(links).toEqual(['Accueil', 'Routine', 'À valider', 'Documents', 'Placements', 'Objectifs', 'Biens', 'Crédits', 'Simulations', 'Revenus', 'Impôts', 'Abonnements', 'Garde']);
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
    expect(groups).toEqual([['Accueil', 'Routine', 'À valider', 'Documents'], ['Placements', 'Objectifs', 'Biens', 'Crédits', 'Simulations'], ['Revenus', 'Impôts', 'Abonnements', 'Garde']]);
  });

  it('shows the API version when hovering the web version', async () => {
    server.use(http.get('*/api/v1/platform/version', () => HttpResponse.json({ version: '1.2.3' })));
    const { user } = await renderApp('/subscriptions');
    await user.hover(await within(sidebar()).findByRole('button', { name: 'Version dev' }));
    // The tooltip opens on « API : … » while the version is still loading.
    await waitFor(async () => expect(await screen.findByRole('tooltip')).toHaveTextContent('API : 1.2.3'));
  });

  it('lists what changed in each version on a click on the version', async () => {
    server.use(http.get('*/changelog.json', () => HttpResponse.json([
      { version: '0.19.0', date: '2026-10-29', changes: [{ en: 'A click on the version lists the changes.', fr: 'Un clic sur la version liste les nouveautés.' }] },
      { version: '0.18.0', date: '2026-10-20', changes: [{ en: 'The tax page keeps its scroll.', fr: 'La page Impôts garde sa position.' }] },
    ])));
    const { user } = await renderApp('/subscriptions');
    await user.click(await within(sidebar()).findByRole('button', { name: 'Version dev' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nouveautés' });
    expect(await within(dialog).findAllByRole('heading', { level: 3 })).toHaveLength(2);
    expect(within(dialog).getAllByRole('heading', { level: 3 })[0]).toHaveTextContent('Version 0.19.0');
    expect(dialog).toHaveTextContent('29 octobre 2026');
    expect(dialog).toHaveTextContent('Un clic sur la version liste les nouveautés.');
    expect(dialog).not.toHaveTextContent('The tax page');
    await expectNoAxeViolations(dialog);
    await user.click(within(dialog).getByRole('button', { name: 'Fermer' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('says there is nothing to show without a changelog (a dev server)', async () => {
    server.use(http.get('*/changelog.json', () => new HttpResponse('<!doctype html>', { headers: { 'Content-Type': 'text/html' } })));
    const { user } = await renderApp('/subscriptions');
    await user.click(await within(sidebar()).findByRole('button', { name: 'Version dev' }));
    expect(await screen.findByText('Aucune nouveauté à afficher.')).toBeInTheDocument();
  });

  it('puts account pages in the person menu', async () => {
    const { user } = await renderApp('/subscriptions');
    await user.click(await openerReady());
    const menu = await screen.findByRole('menu');
    expect(within(menu).getAllByRole('menuitem').map((i) => i.textContent)).toEqual(['Import', 'Export', 'Sécurité', 'Réglages', 'Se déconnecter']);
  });

  it('adds Administration to the person menu for an admin', async () => {
    const { user } = await renderApp('/subscriptions', { capabilities: adminCapabilities });
    await user.click(await openerReady());
    expect(await screen.findByRole('menuitem', { name: 'Administration' })).toBeInTheDocument();
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
    const { router, user } = await renderApp('/income?scope=me');

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
    const heading = await screen.findByRole('heading', { name: 'Cet élément est introuvable.' });
    expect(heading.closest('[data-pane="content"]')).toHaveClass('content-pane');
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
    expect(within(list).getAllByRole('link').map((l) => l.textContent)).toEqual(['À valider', 'Documents', 'Objectifs', 'Biens', 'Crédits', 'Revenus', 'Impôts', 'Simulations', 'Garde', 'Import', 'Export', 'Sécurité', 'Réglages']);
    await expectNoAxeViolations(container);
  });

  it('shows Administration on /more for an admin', async () => {
    await renderApp('/more', { capabilities: adminCapabilities });
    const list = await screen.findByRole('list', { name: 'Autres sections' });
    expect(within(list).getByRole('link', { name: 'Administration' })).toBeInTheDocument();
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

    it('folds the sidebar into a rail and remembers it on this device', async () => {
      const { user } = await renderApp('/subscriptions');
      await user.click(toggle('Replier le menu'));
      expect(sidebar()).toHaveAttribute('data-rail');
      expect(localStorage.getItem('nid.sidebar')).toBe('rail');
      // The names stay for screen readers, and the groups keep their headings.
      expect(within(sidebar()).getAllByRole('link').map((l) => l.textContent)).toEqual(['Accueil', 'Routine', 'À valider', 'Documents', 'Placements', 'Objectifs', 'Biens', 'Crédits', 'Simulations', 'Revenus', 'Impôts', 'Abonnements', 'Garde']);
      expect(within(sidebar()).getAllByRole('heading').map((h) => h.textContent)).toEqual(['Suivi', 'Patrimoine', 'Budget']);
      await user.click(toggle('Déplier le menu'));
      expect(sidebar()).not.toHaveAttribute('data-rail');
      expect(localStorage.getItem('nid.sidebar')).toBe('full');
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
