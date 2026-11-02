import { http, HttpResponse } from 'msw/http';
import { describe, expect, it, vi } from 'vitest';
import { server } from '@/test/server';
import { createApi, CSRF_HEADER } from './client';

describe('createApi', () => {
  it('sends the CSRF token on unsafe methods only', async () => {
    const seen: Record<string, string | null> = {};
    server.use(
      http.patch('*/api/v1/core/me/preferences', ({ request }) => {
        seen.patch = request.headers.get(CSRF_HEADER);
        return HttpResponse.json({ theme: 'sauge', mode: 'system', chartPatterns: false, guidesSeen: {} });
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

  it('turns an empty error body into a Problem carrying the status', async () => {
    server.use(
      http.patch('*/api/v1/core/me/preferences', () => new HttpResponse(null, { status: 403 })),
      http.get('*/api/v1/platform/capabilities', () => new HttpResponse('', { status: 502 })),
    );
    const api = createApi({ getCsrf: () => 'token-1', onUnauthenticated: vi.fn() });

    const patched = await api.fetchClient.PATCH('/api/v1/core/me/preferences', { body: { theme: 'sauge' } });
    const got = await api.fetchClient.GET('/api/v1/platform/capabilities');

    expect(patched.error).toEqual({ status: 403 });
    expect(patched.data).toBeUndefined();
    expect(got.error).toEqual({ status: 502 });
  });

  it('passes a Problem body through unchanged', async () => {
    const problem = { status: 409, code: 'core.conflict', title: 'Conflict', traceId: 'abc' };
    server.use(http.patch('*/api/v1/core/me/preferences', () =>
      HttpResponse.json(problem, { status: 409, headers: { 'content-type': 'application/problem+json' } })));
    const api = createApi({ getCsrf: () => 'token-1', onUnauthenticated: vi.fn() });

    const { error } = await api.fetchClient.PATCH('/api/v1/core/me/preferences', { body: { theme: 'sauge' } });

    expect(error).toEqual(problem);
  });

  it('reports an empty 401 and still yields a Problem', async () => {
    server.use(http.get('*/api/v1/platform/capabilities', () => new HttpResponse(null, { status: 401 })));
    const onUnauthenticated = vi.fn();
    const api = createApi({ getCsrf: () => null, onUnauthenticated });

    const { error } = await api.fetchClient.GET('/api/v1/platform/capabilities');

    expect(onUnauthenticated).toHaveBeenCalledOnce();
    expect(error).toEqual({ status: 401 });
  });
});
