import createFetchClient, { type Middleware } from 'openapi-fetch';
import createQueryHooks from 'openapi-react-query';
import type { paths } from './schema';

export const CSRF_HEADER = 'X-Nid-Csrf';
/** The server's version, on every response: an open tab whose build differs is running the old app. */
export const VERSION_HEADER = 'X-Nid-Version';
const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

export type ApiOptions = {
  getCsrf: () => string | null;
  onUnauthenticated: () => void;
  /** Once, the first time the server answers with a version other than this build's (never in development). */
  onNewVersion?: () => void;
  baseUrl?: string;
};

/** The only way the SPA talks to the API: generated types, one origin, the session cookie (architecture.md §4). */
export function createApi(options: ApiOptions) {
  const fetchClient = createFetchClient<paths>({
    // Same origin behind the proxy; absolute so the request can be built outside a browser too.
    baseUrl: options.baseUrl ?? window.location.origin,
    credentials: 'same-origin',
  });

  let versionChecked = false;
  const middleware: Middleware = {
    onRequest({ request }) {
      const token = options.getCsrf();
      if (token && UNSAFE_METHODS.has(request.method)) request.headers.set(CSRF_HEADER, token);
      return request;
    },
    async onResponse({ response }) {
      if (response.status === 401) options.onUnauthenticated();
      const built = import.meta.env.VITE_NID_VERSION;
      const served = response.headers.get(VERSION_HEADER);
      if (!versionChecked && built && served && served !== built) {
        versionChecked = true;
        options.onNewVersion?.();
      }
      if (response.ok) return response;
      // A bare 401/403/502 (cookie scheme, CSRF filter, proxy) has no body; openapi-fetch would then report
      // no error at all and the caller would take the failure for a success. Give it a Problem with its status.
      if ((await response.clone().text()) !== '') return response;
      return new Response(JSON.stringify({ status: response.status }), {
        status: response.status,
        statusText: response.statusText,
        headers: { 'content-type': 'application/problem+json' },
      });
    },
  };
  fetchClient.use(middleware);

  return { fetchClient, $api: createQueryHooks(fetchClient) };
}

export type Api = ReturnType<typeof createApi>;
