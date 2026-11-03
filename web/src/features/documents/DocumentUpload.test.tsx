import { File as NodeFile } from 'node:buffer';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { CSRF_HEADER } from '@/api/client';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { accepts } from './DocumentUpload';
import { list, summary } from './testData';

type Sent = { name: string; fields: string[]; contentType: string; csrf: string | null };

/** The list (counting its requests) and an upload that refuses some files by name with the given status and code. */
function serve(refuse: Record<string, [number, string]> = {}) {
  const sent: Sent[] = [];
  let gets = 0;
  server.use(
    http.get('*/api/v1/documents', () => { gets += 1; return HttpResponse.json(list([summary()])); }),
    http.post('*/api/v1/documents', async ({ request }) => {
      const contentType = request.headers.get('content-type') ?? '';
      const csrf = request.headers.get(CSRF_HEADER);
      const form = await request.formData();
      const file = form.get('file') as File;
      sent.push({ name: file.name, fields: [...form.keys()], contentType, csrf });
      const refused = refuse[file.name];
      if (refused) return HttpResponse.json({ status: refused[0], code: refused[1], title: 'Refused.' }, { status: refused[0] });
      return HttpResponse.json({ id: crypto.randomUUID() }, { status: 201 });
    }),
  );
  return { sent, gets: () => gets };
}

const pdf = (name: string) => new File(['%PDF-1.7'], name, { type: 'application/pdf' });

async function drop(...files: File[]) {
  const zone = (await screen.findByText('Glissez vos fichiers ici')).closest('[data-drop-zone]');
  if (!zone) throw new Error('no drop zone');
  fireEvent.drop(zone, { dataTransfer: { files } });
}

describe('DocumentUpload', () => {
  beforeAll(async () => {
    // jsdom's FormData and File do not cross into Node's Request (MSW): a browser sends them as is, the tests use Node's.
    const form = await new Response('a=1', { headers: { 'content-type': 'application/x-www-form-urlencoded' } }).formData();
    vi.stubGlobal('FormData', form.constructor);
    vi.stubGlobal('File', NodeFile);
    await import('./DocumentsPage');
  }, 30_000);
  afterAll(() => { vi.unstubAllGlobals(); });

  it('keeps its live region on the page before any upload, so the first outcome is announced', async () => {
    serve();
    await renderApp('/documents');
    const region = await screen.findByRole('list', { name: 'Dépôts' });
    expect(region).toHaveAttribute('aria-live', 'polite');
    expect(region).toBeEmptyDOMElement();
  });

  it('posts each dropped file as its own multipart form, then refreshes the list', async () => {
    const api = serve();
    await renderApp('/documents');
    await screen.findByRole('table', { name: 'Liste des documents' });
    const before = api.gets();

    await drop(pdf('bail.pdf'), pdf('avis.pdf'));

    await waitFor(() => expect(api.sent.map((s) => s.name).sort()).toEqual(['avis.pdf', 'bail.pdf']));
    for (const s of api.sent) {
      expect(s.contentType).toMatch(/^multipart\/form-data; boundary=/);
      expect(s.fields).toEqual(['file', 'warnDuplicate']);
      expect(s.csrf).toBe('csrf-alice');
    }
    expect(await screen.findAllByText('Déposé')).toHaveLength(2);
    await waitFor(() => expect(api.gets()).toBeGreaterThan(before));
  });

  it('shows a refused file by name and still uploads the others', async () => {
    const api = serve({ 'notes.txt': [415, 'documents.unsupported_type'], 'scan.pdf': [413, 'documents.too_large'] });
    await renderApp('/documents');
    await drop(new File(['hello'], 'notes.txt', { type: 'text/plain' }), pdf('bail.pdf'), pdf('scan.pdf'));

    expect(await screen.findByText('notes.txt : ce type de fichier n’est pas pris en charge (PDF, PNG, JPEG ou e-mail .eml).')).toBeInTheDocument();
    expect(await screen.findByText('scan.pdf : le fichier dépasse la taille permise (25 Mo).')).toBeInTheDocument();
    expect(await screen.findAllByText('Déposé')).toHaveLength(1);
    // The text file is refused before it is sent; the API refuses the large one.
    expect(api.sent.map((s) => s.name).sort()).toEqual(['bail.pdf', 'scan.pdf']);
  });

  it('names each file already in Nid, uploads the others, and lets each be kept or added anyway', async () => {
    const existing = summary({ title: 'Bail 2024', links: [] });
    const sent: { name: string; warn: FormDataEntryValue | null }[] = [];
    server.use(
      http.get('*/api/v1/documents', () => HttpResponse.json(list([summary()]))),
      http.post('*/api/v1/documents', async ({ request }) => {
        const form = await request.formData();
        const name = (form.get('file') as File).name;
        sent.push({ name, warn: form.get('warnDuplicate') });
        return name !== 'avis.pdf' && form.get('warnDuplicate') === 'true'
          ? HttpResponse.json({ status: 409, code: 'documents.duplicate', title: 'Duplicate.', duplicate: existing }, { status: 409 })
          : HttpResponse.json({ id: crypto.randomUUID() }, { status: 201 });
      }),
    );
    const { user } = await renderApp('/documents');
    await drop(pdf('bail.pdf'), pdf('avis.pdf'), pdf('copie.pdf'));

    await waitFor(() => expect(screen.getAllByRole('alert')).toHaveLength(2));
    const [bail, copie] = screen.getAllByRole('alert') as [HTMLElement, HTMLElement];
    expect(bail).toHaveTextContent('bail.pdf est déjà dans Nid :« Bail 2024 »');
    expect(copie).toHaveTextContent('copie.pdf est déjà dans Nid :');
    expect(screen.getByText('Déposé')).toBeInTheDocument();

    await user.click(within(bail).getByRole('button', { name: 'Garder l’existant' }));
    expect(await screen.findByText('Existant gardé')).toBeInTheDocument();
    await user.click(within(copie).getByRole('button', { name: 'Ajouter quand même' }));
    await waitFor(() => expect(screen.getAllByText('Déposé')).toHaveLength(2));
    expect(sent).toEqual([
      { name: 'bail.pdf', warn: 'true' }, { name: 'avis.pdf', warn: 'true' }, { name: 'copie.pdf', warn: 'true' }, { name: 'copie.pdf', warn: null },
    ]);
  });

  it('takes a file by its extension or its media type, as the picker does', () => {
    const accept = '.pdf,image/png,image/*';
    expect(accepts(accept, new File([''], 'BAIL.PDF'))).toBe(true);
    expect(accepts(accept, new File([''], 'scan', { type: 'image/png' }))).toBe(true);
    expect(accepts(accept, new File([''], 'photo.heic', { type: 'image/heic' }))).toBe(true);
    expect(accepts('.pdf,image/png', new File([''], 'notes.txt', { type: 'text/plain' }))).toBe(false);
  });

  it('the picker takes several files', async () => {
    const api = serve();
    const { user } = await renderApp('/documents');
    await user.upload(await screen.findByLabelText('Déposer'), [pdf('a.pdf'), pdf('b.pdf')]);
    await waitFor(() => expect(api.sent).toHaveLength(2));
  });

  it('sends the files one at a time: the next request starts once the previous one answered', async () => {
    const events: string[] = [];
    let release: () => void = () => undefined;
    server.use(
      http.get('*/api/v1/documents', () => HttpResponse.json(list([summary()]))),
      http.post('*/api/v1/documents', async ({ request }) => {
        const name = ((await request.formData()).get('file') as File).name;
        events.push(`start ${name}`);
        await new Promise<void>((resolve) => { release = resolve; });
        events.push(`end ${name}`);
        return HttpResponse.json({ id: crypto.randomUUID() }, { status: 201 });
      }),
    );
    await renderApp('/documents');
    await drop(pdf('a.pdf'), pdf('b.pdf'), pdf('c.pdf'));

    await waitFor(() => expect(events).toEqual(['start a.pdf']));
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(events).toEqual(['start a.pdf']);
    release();
    await waitFor(() => expect(events).toEqual(['start a.pdf', 'end a.pdf', 'start b.pdf']));
    release();
    await waitFor(() => expect(events.at(-1)).toBe('start c.pdf'));
    release();
    await waitFor(() => expect(screen.getAllByText('Déposé')).toHaveLength(3));
  });

  it('accepts e-mails in the picker', async () => {
    serve();
    await renderApp('/documents');
    expect((await screen.findByText('Déposer')).querySelector('input')).toHaveAttribute('accept', expect.stringContaining('.eml'));
  });
});
