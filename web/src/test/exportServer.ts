import { http, HttpResponse } from 'msw';

type Failure = { section: string; page: number; status: number; code?: string };

/**
 * 1D2's export protocol in memory: `pages` gives each section's pages in order (a section with no entity is `[[]]`).
 * Cursors are `c1`, `c2`…, the index of the next page. Tests move `state.revision`, fail a page, or hold one.
 */
export function fakeExportServer(pages: Record<string, unknown[][]>) {
  const state = {
    revision: 'r1',
    calls: [] as string[],
    failWith: null as Failure | null,
    onPage: null as ((section: string, page: number) => void | Promise<void>) | null,
  };
  const handlers = [
    http.get('*/api/v1/platform/export/manifest', () => HttpResponse.json({
      format: 'nid.data',
      version: 1,
      exportedAt: '2026-09-26T10:00:00+00:00',
      revision: state.revision,
      sections: Object.entries(pages).map(([name, list]) => ({ name, count: list.flat().length })),
    })),
    http.get('*/api/v1/platform/export/:section', async ({ params, request }) => {
      const section = String(params.section);
      const cursor = new URL(request.url).searchParams.get('cursor');
      const page = cursor ? Number(cursor.slice(1)) : 0;
      state.calls.push(`${section}:${cursor ?? ''}`);
      await state.onPage?.(section, page);
      const fail = state.failWith;
      if (fail && fail.section === section && fail.page === page) {
        return HttpResponse.json({ status: fail.status, code: fail.code ?? null, title: 'Refusé' }, {
          status: fail.status, headers: { 'content-type': 'application/problem+json' },
        });
      }
      const list = pages[section] ?? [[]];
      return HttpResponse.json({ section, entities: list[page] ?? [], nextCursor: page + 1 < list.length ? `c${page + 1}` : null });
    }),
  ];
  return { state, handlers };
}
