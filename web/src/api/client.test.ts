import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '@/test/server';
import { createApi, CSRF_HEADER } from './client';

describe('createApi', () => {
  it('sends the CSRF token on unsafe methods only', async () => {
    const seen: Record<string, string | null> = {};
    server.use(
      http.patch('*/api/v1/core/me/preferences', ({ request }) => {
        seen.patch = request.headers.get(CSRF_HEADER);
        return HttpResponse.json({ theme: 'sauge', mode: 'system', chartPatterns: false });
      }),
      http.get('*/api/v1/platform/capabilities', ({ request }) => {
        seen.get = request.headers.get(CSRF_HEADER);
        return HttpResponse.json({ modules: [] });
      }),
    );
    const api = createApi({ getCsrf: () => 'token-1', onUnauthenticated: vi.fn() });

    await api.fetchClient.PATCH('/api/v1/core/me/preferences', { body: { theme: 'sauge' } });
    await api.fetchClient.GET('/api/v1/platform/capabilities');

    expect(seen).toEqual({ patch: 'token-1', get: null });
  });

  it('reports a 401 so the session can be cleared', async () => {
    server.use(http.get('*/api/v1/platform/capabilities', () => new HttpResponse(null, { status: 401 })));
    const onUnauthenticated = vi.fn();
    const api = createApi({ getCsrf: () => null, onUnauthenticated });

    await api.fetchClient.GET('/api/v1/platform/capabilities');

    expect(onUnauthenticated).toHaveBeenCalledOnce();
  });
});
