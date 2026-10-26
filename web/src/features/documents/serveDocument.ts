import { http, HttpResponse } from 'msw';
import { server } from '@/test/server';
import type { DocumentDetail, DocumentTargetList } from './api';
import { detail, targets as allTargets } from './testData';

export type Sent = { method: string; path: string; search: string; body: unknown };

/**
 * One document behind the detail routes, kept as the writes leave it (archive, role, unlink, link), with every write and
 * preview recorded. `preview` answers the readers a new link would add; `put` may refuse the edit.
 */
export function serveDocument(over: {
  doc?: DocumentDetail; targets?: DocumentTargetList; preview?: { id: string; name: string }[]; put?: () => Response;
} = {}) {
  let doc = over.doc ?? detail();
  const sent: Sent[] = [];
  const record = async (request: Request) => {
    const url = new URL(request.url);
    const text = request.method === 'GET' || request.method === 'DELETE' ? '' : await request.text();
    sent.push({ method: request.method, path: url.pathname, search: url.search, body: text ? JSON.parse(text) : undefined });
  };
  const set = (patch: Partial<DocumentDetail['document']>) => { doc = { ...doc, document: { ...doc.document, ...patch } }; };
  server.use(
    http.get('*/api/v1/documents/targets', () => HttpResponse.json(over.targets ?? allTargets())),
    http.get('*/api/v1/documents/:id/links/preview', async ({ request }) => {
      await record(request);
      return HttpResponse.json({ gainedReaders: over.preview ?? [] });
    }),
    http.get('*/api/v1/documents/:id', () => HttpResponse.json(doc)),
    http.put('*/api/v1/documents/:id', async ({ request }) => {
      await record(request);
      return over.put?.() ?? HttpResponse.json({ id: doc.document.id });
    }),
    http.post('*/api/v1/documents/:id/archive', async ({ request }) => { await record(request); set({ archived: true }); return new HttpResponse(null, { status: 204 }); }),
    http.post('*/api/v1/documents/:id/unarchive', async ({ request }) => { await record(request); set({ archived: false }); return new HttpResponse(null, { status: 204 }); }),
    http.post('*/api/v1/documents/:id/links', async ({ request }) => {
      await record(request);
      const body = sent.at(-1)!.body as { targetKind: string; targetId: string; role: string };
      set({ links: [...doc.document.links, { ...body, label: 'Nouveau lien', canWrite: true }] });
      return HttpResponse.json({ gainedReaders: (over.preview ?? []).map((r) => r.id) });
    }),
    http.put('*/api/v1/documents/:id/links/:kind/:target', async ({ request, params }) => {
      await record(request);
      const { role } = sent.at(-1)!.body as { role: string };
      set({ links: doc.document.links.map((l) => (l.targetId === params.target ? { ...l, role } : l)) });
      return HttpResponse.json({ id: doc.document.id });
    }),
    http.delete('*/api/v1/documents/:id/links/:kind/:target', async ({ request, params }) => {
      await record(request);
      set({ links: doc.document.links.filter((l) => l.targetId !== params.target) });
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return { sent, writes: () => sent.filter((s) => s.method !== 'GET') };
}
