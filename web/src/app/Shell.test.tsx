import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { detail, list, SALARY, summary } from '@/features/income/testData';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const sidebar = () => screen.getByRole('navigation', { name: 'Navigation principale' });
const tabs = () => screen.getByRole('navigation', { name: 'Onglets' });

describe('the shell', () => {
  it('lists the enabled modules and settings', async () => {
    await renderApp('/subscriptions');
    const links = within(sidebar()).getAllByRole('link').map((l) => l.textContent);
    expect(links).toEqual(['Accueil', 'Abonnements', 'Placements', 'Revenus', 'Biens', 'Crédits', 'Routine', 'Import', 'Export', 'Sécurité', 'Réglages']);
  });

  it('offers Export even where import is disabled', async () => {
    await renderApp('/subscriptions', { capabilities: { modules: ['core', 'subscriptions'], admin: false } });
    expect(within(sidebar()).getByRole('link', { name: 'Export' })).toBeInTheDocument();
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

  it('keeps Moi through a link to a detail and through « Retour »', async () => {
    server.use(
      http.get('*/api/v1/income/sources', () => HttpResponse.json(list([summary()]))),
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
    );
    const { router, user } = await renderApp('/income?scope=me');

    await user.click(await screen.findByRole('link', { name: /Salaire Alice/ }));
    expect(router.state.location.pathname).toBe(`/income/${SALARY}`);
    expect(router.state.location.search).toEqual({ scope: 'me' });

    await screen.findByRole('heading', { level: 1, name: 'Salaire Alice' }, { timeout: 5000 });
    await user.click(await screen.findByRole('link', { name: 'Retour aux revenus' }));
    expect(router.state.location.pathname).toBe('/income');
    expect(router.state.location.search).toEqual({ scope: 'me' });
  });

  it('the selector back to Foyer removes the scope', async () => {
    const { router, user } = await renderApp('/subscriptions?scope=me');
    await user.click(screen.getByRole('radio', { name: 'Foyer' }));
    expect(router.state.location.search).toEqual({});
    await user.click(within(sidebar()).getByRole('link', { name: 'Import' }));
    expect(router.state.location.search).toEqual({});
  });

  it('keeps Foyer / Moi in the URL across navigation', async () => {
    const { router, user } = await renderApp('/subscriptions');
    expect(screen.getByRole('radio', { name: 'Foyer' })).toBeChecked();

    await user.click(screen.getByRole('radio', { name: 'Moi' }));
    expect(router.state.location.search).toEqual({ scope: 'me' });

    await user.click(within(sidebar()).getByRole('link', { name: 'Import' }));
    expect(router.state.location.pathname).toBe('/import');
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

    await user.click(screen.getByRole('button', { name: 'Se déconnecter' }));

    expect(csrf).toBe('csrf-alice');
    expect(location.assign).toHaveBeenCalledWith('/signed-out');
  });

  it('stays when the sign-out is refused', async () => {
    server.use(http.post('*/auth/logout', () => new HttpResponse(null, { status: 403 })));
    const { location, user } = await renderApp('/subscriptions');

    await user.click(screen.getByRole('button', { name: 'Se déconnecter' }));

    expect(location.assign).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Se déconnecter' })).toBeInTheDocument();
  });

  it('has no accessibility violation', async () => {
    const { container } = await renderApp('/subscriptions');
    await expectNoAxeViolations(container);
  });
  it('shows four tabs and Plus on a phone', async () => {
    await renderApp('/subscriptions');
    expect(within(tabs()).getAllByRole('link').map((l) => l.textContent)).toEqual(['Accueil', 'Abonnements', 'Placements', 'Revenus', 'Plus']);
  });

  it('the tab bar follows the enabled modules', async () => {
    await renderApp('/subscriptions', { capabilities: { modules: ['core', 'subscriptions', 'investments'], admin: false } });
    expect(within(tabs()).getAllByRole('link').map((l) => l.textContent)).toEqual(['Accueil', 'Abonnements', 'Placements', 'Plus']);
  });

  it('lists the other sections on /more, Administration for an admin only', async () => {
    const { container } = await renderApp('/more');
    const list = await screen.findByRole('list', { name: 'Autres sections' });
    expect(within(list).getAllByRole('link').map((l) => l.textContent)).toEqual(['Biens', 'Crédits', 'Routine', 'Import', 'Export', 'Sécurité', 'Réglages']);
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
