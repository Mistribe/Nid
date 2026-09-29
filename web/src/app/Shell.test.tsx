import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const sidebar = () => screen.getByRole('navigation', { name: 'Navigation principale' });

describe('the shell', () => {
  it('lists the enabled modules and settings', async () => {
    await renderApp('/subscriptions');
    const links = within(sidebar()).getAllByRole('link').map((l) => l.textContent);
    expect(links).toEqual(['Abonnements', 'Placements', 'Import', 'Export', 'Sécurité', 'Réglages']);
  });

  it('offers Export even where import is disabled', async () => {
    await renderApp('/subscriptions', { capabilities: { modules: ['core', 'subscriptions'], admin: false } });
    expect(within(sidebar()).getByRole('link', { name: 'Export' })).toBeInTheDocument();
  });

  it('hides a module the server does not enable, and its route sends you home', async () => {
    const { router } = await renderApp('/import', { capabilities: { modules: ['core', 'subscriptions'], admin: false } });
    expect(within(sidebar()).queryByRole('link', { name: 'Import' })).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/subscriptions');
  });

  it('opens on the first module', async () => {
    const { router } = await renderApp('/');
    expect(router.state.location.pathname).toBe('/subscriptions');
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
});
