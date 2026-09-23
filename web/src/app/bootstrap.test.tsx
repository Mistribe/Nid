import { act, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { aliceSessionResponse, signedOutResponse } from '@/test/fixtures';
import { server } from '@/test/server';
import { bootstrap } from './bootstrap';
import { loginUrl } from './session';

function fakeLocation(pathname: string, search = '') {
  return { pathname, search, assign: vi.fn() };
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
      ...aliceSessionResponse, preferences: { theme: 'sauge', mode: 'dark', chartPatterns: false },
    })));
    localStorage.setItem('nid.theme', 'terre'); // an older local copy: the server wins
    const root = document.body.appendChild(document.createElement('div'));
    container = root;

    const reactRoot = await bootstrap({ root, location: fakeLocation('/') });

    expect(await screen.findByText('Nid')).toBeInTheDocument();
    expect(document.documentElement.dataset.theme).toBe('sauge');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
    expect(localStorage.getItem('nid.theme')).toBe('sauge');

    act(() => reactRoot?.unmount());
  });
});
