import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { dashboard, todoItem } from '@/features/dashboard/testData';
import { detail, list, SALARY, summary } from '@/features/income/testData';
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
    expect(links).toEqual(['Accueil', 'Routine', 'À valider', 'Placements', 'Biens', 'Crédits', 'Revenus', 'Impôts', 'Abonnements']);
  });

  it('groups the sidebar into Suivi, Patrimoine and Budget', async () => {
    await renderApp('/subscriptions');
    const nav = within(sidebar());
    expect(nav.getAllByRole('heading').map((h) => h.textContent)).toEqual(['Suivi', 'Patrimoine', 'Budget']);
    const groups = nav.getAllByRole('list').map((l) => within(l).getAllByRole('link').map((a) => a.textContent));
    expect(groups).toEqual([['Accueil', 'Routine', 'À valider'], ['Placements', 'Biens', 'Crédits'], ['Revenus', 'Impôts', 'Abonnements']]);
  });

  it('shows the API version when hovering the web version', async () => {
    server.use(http.get('*/api/v1/platform/version', () => HttpResponse.json({ version: '1.2.3' })));
    const { user } = await renderApp('/subscriptions');
    await user.hover(await within(sidebar()).findByRole('button', { name: 'Version dev' }));
    expect(await screen.findByRole('tooltip')).toHaveTextContent('API : 1.2.3');
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
      http.get('*/api/v1/income/sources', () => HttpResponse.json(list([summary()]))),
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
    );
    const { router, user } = await renderApp('/income?scope=me');

    await user.click((await screen.findAllByRole('link', { name: /Salaire Alice/ }))[0]!);
    expect(router.state.location.pathname).toBe(`/income/${SALARY}`);
    expect(router.state.location.search).toEqual({ scope: 'me' });

    await screen.findByRole('heading', { level: 1, name: 'Salaire Alice' }, { timeout: 5000 });
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
    expect(screen.getByRole('radio', { name: 'Foyer' })).toBeChecked();

    await user.click(screen.getByRole('radio', { name: 'Moi' }));
    expect(router.state.location.search).toEqual({ scope: 'me' });

    await user.click(within(sidebar()).getByRole('link', { name: 'Placements' }));
    expect(router.state.location.pathname).toBe('/investments');
    expect(router.state.location.search).toEqual({ scope: 'me' });
    expect(screen.getByRole('radio', { name: 'Moi' })).toBeChecked();
  });

  it('shows the not-found page for an unknown path', async () => {
    await renderApp('/nowhere');
    expect(await screen.findByRole('heading', { name: 'Cet élément est introuvable.' })).toBeInTheDocument();
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
    expect(within(list).getAllByRole('link').map((l) => l.textContent)).toEqual(['À valider', 'Biens', 'Crédits', 'Revenus', 'Impôts', 'Import', 'Export', 'Sécurité', 'Réglages']);
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
});
