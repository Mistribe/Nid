import { act, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Root } from 'react-dom/client';
import { expectNoAxeViolations } from '@/test/axe';
import { aliceSessionResponse, signedOutResponse } from '@/test/fixtures';
import { server } from '@/test/server';
import { bootstrap } from './bootstrap';
import { loginUrl } from './session';

function fakeLocation(pathname: string, search = '') {
  return { pathname, search, assign: vi.fn(), reload: vi.fn() };
}

describe('loginUrl', () => {
  it('returns to where the user was, as a relative path', () =>
    expect(loginUrl({ pathname: '/subscriptions', search: '?scope=me' }))
      .toBe('/auth/login?returnUrl=%2Fsubscriptions%3Fscope%3Dme'));
});

describe('bootstrap', () => {
  let container: HTMLElement | null = null;

  afterEach(() => {
    container?.remove();
    container = null;
  });

  it('sends a signed-out visitor to login and renders nothing', async () => {
    server.use(http.get('*/auth/session', () => HttpResponse.json(signedOutResponse)));
    const root = document.body.appendChild(document.createElement('div'));
    container = root;
    const location = fakeLocation('/subscriptions', '?scope=me');

    const reactRoot = await bootstrap({ root, location });

    expect(location.assign).toHaveBeenCalledWith('/auth/login?returnUrl=%2Fsubscriptions%3Fscope%3Dme');
    expect(root).toBeEmptyDOMElement();
    expect(reactRoot).toBeNull();
  });

  it('applies the server preferences, remembers them, and renders the app', async () => {
    server.use(http.get('*/auth/session', () => HttpResponse.json({
      ...aliceSessionResponse, preferences: { theme: 'sauge', mode: 'dark', chartPatterns: false, guidesSeen: {} },
    })));
    localStorage.setItem('nid.theme', 'terre'); // an older local copy: the server wins
    const root = document.body.appendChild(document.createElement('div'));
    container = root;

    const reactRoot = await bootstrap({ root, location: fakeLocation('/') });

    expect(await screen.findAllByText('Nid')).toHaveLength(2);
    expect(document.documentElement.dataset.theme).toBe('sauge');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
    expect(localStorage.getItem('nid.theme')).toBe('sauge');

    act(() => reactRoot?.unmount());
  });

  it('says so and offers to retry when the session cannot be read', async () => {
    server.use(http.get('*/auth/session', () => HttpResponse.json({ status: 500, traceId: '00-t-05' }, { status: 500 })));
    const root = document.body.appendChild(document.createElement('div'));
    container = root;
    const location = fakeLocation('/subscriptions');

    const reactRoot = await bootstrap({ root, location });

    expect(await screen.findByRole('alert')).toHaveTextContent('Une erreur inattendue est survenue');
    expect(screen.getByRole('alert')).toHaveTextContent('00-t-05');
    expect(screen.getAllByRole('button').map((b) => b.textContent)).toEqual(['Réessayer']);
    expect(screen.queryByRole('navigation')).not.toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(location.assign).not.toHaveBeenCalled();
    await expectNoAxeViolations(root);

    await userEvent.setup().click(screen.getByRole('button', { name: 'Réessayer' }));
    expect(location.reload).toHaveBeenCalledOnce();

    act(() => reactRoot?.unmount());
  });

  it('says so when the API cannot be reached at all', async () => {
    server.use(http.get('*/auth/session', () => HttpResponse.error()));
    const root = document.body.appendChild(document.createElement('div'));
    container = root;

    const reactRoot = await bootstrap({ root, location: fakeLocation('/') });

    expect(await screen.findByRole('alert')).toHaveTextContent('Une erreur inattendue est survenue');
    expect(screen.getByRole('button', { name: 'Réessayer' })).toBeInTheDocument();

    act(() => reactRoot?.unmount());
  });

  it('never guesses the modules when the capabilities cannot be read', async () => {
    server.use(http.get('*/api/v1/platform/capabilities', () => new HttpResponse(null, { status: 500 })));
    const root = document.body.appendChild(document.createElement('div'));
    container = root;

    const reactRoot = await bootstrap({ root, location: fakeLocation('/') });

    expect(await screen.findByRole('alert')).toHaveTextContent('Une erreur inattendue est survenue');
    expect(screen.getByRole('button', { name: 'Réessayer' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Se déconnecter' })).not.toBeInTheDocument();

    act(() => reactRoot?.unmount());
  });

  it('shows the signed-out page without sending you back to login', async () => {
    server.use(http.get('*/auth/session', () =>
      HttpResponse.json({ ...signedOutResponse, identityProvider: 'https://auth.example.test' })));
    const root = document.body.appendChild(document.createElement('div'));
    container = root;
    const location = fakeLocation('/signed-out');

    const reactRoot = await bootstrap({ root, location });

    expect(await screen.findByRole('heading', { name: 'Vous êtes déconnecté de Nid' })).toBeInTheDocument();
    expect(screen.getByText(/reste ouverte sur cet appareil/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: "Ouvrir le portail d'identité" })).toHaveAttribute('href', 'https://auth.example.test');
    expect(screen.getByRole('link', { name: 'Se reconnecter' })).toHaveAttribute('href', '/auth/login?returnUrl=%2F');
    expect(location.assign).not.toHaveBeenCalled();
    await expectNoAxeViolations(root);

    act(() => reactRoot?.unmount());
  });

  it('offers no portal link when the identity provider is not known', async () => {
    server.use(http.get('*/auth/session', () => HttpResponse.error()));
    const root = document.body.appendChild(document.createElement('div'));
    container = root;
    const location = fakeLocation('/signed-out');

    const reactRoot = await bootstrap({ root, location });

    expect(await screen.findByRole('heading', { name: 'Vous êtes déconnecté de Nid' })).toBeInTheDocument();
    expect(screen.getAllByRole('link').map((l) => l.textContent)).toEqual(['Se reconnecter']);
    expect(location.assign).not.toHaveBeenCalled();

    act(() => reactRoot?.unmount());
  });

  it('renders the refusal page with the email received, and never the login redirect', async () => {
    const root = document.body.appendChild(document.createElement('div'));
    container = root;
    const location = fakeLocation('/login-refused', '?email=carol%40example.test');

    let reactRoot: Root | null = null;
    await act(async () => { reactRoot = await bootstrap({ root, location }); });

    expect(location.assign).not.toHaveBeenCalled();
    expect(await screen.findByRole('heading', { name: 'Vous ne pouvez pas encore entrer dans Nid' })).toBeInTheDocument();
    expect(screen.getByText(/carol@example\.test/)).toBeInTheDocument();
    await expectNoAxeViolations(root);

    act(() => reactRoot?.unmount());
  });

  it('says so when the identity provider sent no email', async () => {
    const root = document.body.appendChild(document.createElement('div'));
    container = root;

    let reactRoot: Root | null = null;
    await act(async () => { reactRoot = await bootstrap({ root, location: fakeLocation('/login-refused') }); });

    expect(await screen.findByText(/aucune adresse email/)).toBeInTheDocument();

    act(() => reactRoot?.unmount());
  });
});
