import { File as NodeFile } from 'node:buffer';
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { createApi } from '@/api/client';
import { createQueryClient } from '@/app/bootstrap';
import { Providers } from '@/app/Providers';
import { Toaster } from '@/components/ui/sonner';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { aliceSession, allCapabilities } from '@/test/fixtures';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import type { DocumentSummary } from '@/api/documents';
import { ItemDocuments } from './ItemDocuments';
import { ITEM, link, list, summary } from './testData';

const id = (n: number) => `0192f0aa-0000-7000-8000-00000000d00${n}`;
const [D1, D2, D3, D4, D5, D6] = [1, 2, 3, 4, 5, 6].map(id) as [string, string, string, string, string, string];
const P1 = '0192f0aa-0000-7000-8000-0000000000a1';
const OTHER_ITEM = '0192f0aa-0000-7000-8000-00000000e00f';

const phaseLink = (over = {}) => link({ targetKind: 'income.phase', targetId: P1, role: 'payslip', label: 'Bulletin 09/2026', ...over });
/** D1 on the item, on one of its phases and on the household: two rows, the household has none. */
const d1 = (over: Partial<DocumentSummary> = {}) => summary({
  id: D1, title: 'Bulletin septembre', documentDate: '2026-09-30', links: [
    link({ role: 'payslip' }), phaseLink(),
    link({ targetKind: 'core.household', targetId: '0192f0aa-0000-7000-8000-0000000000f0', anchorItemId: null, role: 'tax_notice', label: 'Foyer' }),
  ], ...over,
});
const older = summary({ id: D6, title: 'Relevé août', documentDate: '2026-08-31', links: [link({ role: 'statement' })] });

/** The anchored list (counting its requests), the candidates of `scope=me`, the preview and every write. */
function serve({ anchored = [d1(), older], mine = [] as DocumentSummary[], preview = [] as { id: string; name: string }[], upload = (() => HttpResponse.json({ id: D2 }, { status: 201 })) as (name: string) => Response } = {}) {
  let gets = 0;
  const sent: string[] = [];
  const writes: { method: string; path: string; body: unknown }[] = [];
  server.use(
    http.get('*/api/v1/documents', ({ request }) => {
      if (new URL(request.url).searchParams.get('anchor') === ITEM) { gets += 1; return HttpResponse.json(list(anchored)); }
      return HttpResponse.json(list(mine));
    }),
    http.post('*/api/v1/documents', async ({ request }) => {
      const form = await request.formData();
      const name = (form.get('file') as File).name;
      sent.push(name);
      writes.push({ method: 'POST', path: '/api/v1/documents', body: Object.fromEntries([...form.entries()].filter(([k]) => k !== 'file')) });
      return upload(name);
    }),
    http.get('*/api/v1/documents/:id/links/preview', () => HttpResponse.json({ gainedReaders: preview })),
    http.post('*/api/v1/documents/:id/links', async ({ request }) => {
      writes.push({ method: 'POST', path: new URL(request.url).pathname, body: await request.json() });
      return HttpResponse.json({ gainedReaders: [] });
    }),
    http.delete('*/api/v1/documents/:id/links/:kind/:target', ({ request }) => {
      writes.push({ method: 'DELETE', path: new URL(request.url).pathname, body: undefined });
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return { writes, sent, gets: () => gets };
}

/** The component alone under the real providers; its title links need a router, its toasts the shell's Toaster. */
async function renderItem(props: { canWrite?: boolean; defaultRole?: string } = {}) {
  const queryClient = createQueryClient();
  const api = createApi({ getCsrf: () => aliceSession.csrf, onUnauthenticated: vi.fn() });
  const location = { pathname: '/', search: '', assign: vi.fn(), reload: vi.fn() };
  const ui = <ItemDocuments itemId={ITEM} canWrite={props.canWrite ?? true} defaultRole={props.defaultRole ?? 'statement'} />;
  const router = createRouter({ routeTree: createRootRoute({ component: () => ui }), history: createMemoryHistory() });
  await router.load();
  const utils = render(
    <Providers api={api} queryClient={queryClient} session={aliceSession} capabilities={allCapabilities} location={location}>
      <RouterProvider router={router} />
      <Toaster containerAriaLabel="Messages" />
    </Providers>,
  );
  return { ...utils, user: userEvent.setup() };
}

const table = async () => screen.findByRole('table', { name: 'Documents' });
const rows = async () => within(await table()).getAllByRole('row').slice(1);
const pdf = (name = 'releve.pdf') => new File(['%PDF-1.7'], name, { type: 'application/pdf' });

async function openMenu(user: ReturnType<typeof userEvent.setup>, rowText: string) {
  const row = (await rows()).find((r) => r.textContent?.includes(rowText));
  if (!row) throw new Error(`no row ${rowText}`);
  await user.click(within(row).getByRole('button', { name: /^Actions du document/ }));
  return screen.findByRole('menu');
}

describe('ItemDocuments', () => {
  beforeAll(async () => {
    // jsdom's FormData and File do not cross into Node's Request (MSW): a browser sends them as is, the tests use Node's.
    const form = await new Response('a=1', { headers: { 'content-type': 'application/x-www-form-urlencoded' } }).formData();
    vi.stubGlobal('FormData', form.constructor);
    vi.stubGlobal('File', NodeFile);
  });
  afterAll(() => { vi.unstubAllGlobals(); });

  it('has one row per link anchored on the item, newest first, and none for the household', async () => {
    serve();
    await renderItem();
    const lines = await rows();
    expect(lines.map((r) => within(r).getAllByRole('cell')[3]!.textContent)).toEqual(['Cette fiche', 'Bulletin 09/2026', 'Cette fiche']);
    expect(lines[0]).toHaveTextContent('Bulletin septembre');
    expect(lines[0]).toHaveTextContent('Bulletin de paie');
    expect(lines[2]).toHaveTextContent('Relevé août');
    expect(await table()).not.toHaveTextContent('Foyer');
    expect(within(lines[0]!).getByRole('link', { name: 'Bulletin septembre' })).toHaveAttribute('href', `/documents/${D1}`);
  });

  it('has no row for a link anchored on another item, so no Retirer of it', async () => {
    const foreign = phaseLink({ targetId: '0192f0aa-0000-7000-8000-0000000000a2', anchorItemId: OTHER_ITEM, label: 'Bulletin 10/2026' });
    serve({ anchored: [summary({ id: D2, title: 'Avis', links: [link({ role: 'payslip' }), foreign] })] });
    await renderItem();
    const lines = await rows();
    expect(lines).toHaveLength(1);
    expect(await table()).not.toHaveTextContent('Bulletin 10/2026');
  });

  it('says so when the item has no document', async () => {
    serve({ anchored: [] });
    await renderItem();
    expect(await screen.findByText('Aucun document.')).toBeInTheDocument();
  });

  it('a reader can download and open, not upload or link', async () => {
    serve();
    const { user } = await renderItem({ canWrite: false });
    const menu = await openMenu(user, 'Relevé août');
    expect(within(menu).getByRole('menuitem', { name: 'Télécharger' })).toHaveAttribute('href', `/api/v1/documents/${D6}/content`);
    expect(within(menu).getByRole('menuitem', { name: 'Voir le document' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Déposer', hidden: true })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Relier un document', hidden: true })).not.toBeInTheDocument();
  });

  it('« Voir » opens the document in a dialog that keeps the download link and closes on Escape (#413)', async () => {
    serve();
    const { user } = await renderItem({ canWrite: false });
    const menu = await openMenu(user, 'Relevé août');
    await user.click(within(menu).getByRole('menuitem', { name: 'Voir' }));

    const dialog = await screen.findByRole('dialog', { name: 'Relevé août' });
    expect(within(dialog).getByTitle('Relevé août')).toHaveAttribute('src', `/api/v1/documents/${D6}/content?inline=true`);
    const download = within(dialog).getByRole('link', { name: 'Télécharger' });
    expect(download).toHaveAttribute('href', `/api/v1/documents/${D6}/content`);
    await user.tab();
    expect(dialog).toContainElement(document.activeElement as HTMLElement);
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  describe('Déposer', () => {
    it('preselects the type of the page', async () => {
      serve();
      const { user } = await renderItem({ defaultRole: 'loan_offer' });
      await user.click(await screen.findByRole('button', { name: 'Déposer' }));
      const dialog = await screen.findByRole('dialog', { name: 'Déposer un document' });
      expect(within(dialog).getByRole('combobox', { name: 'Type' })).toHaveTextContent('Offre de prêt');
    });

    it('uploads the file onto the item with its type and date', async () => {
      const api = serve();
      const { user } = await renderItem();
      await user.click(await screen.findByRole('button', { name: 'Déposer' }));
      const dialog = await screen.findByRole('dialog', { name: 'Déposer un document' });
      await user.upload(within(dialog).getByLabelText('Fichiers'), pdf());
      await pick(user, 'Type', 'Assurance');
      await user.type(within(dialog).getByRole('textbox', { name: 'Date du document' }), '01/09/2026');
      const before = api.gets();
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));

      await waitFor(() => expect(api.writes).toEqual([{ method: 'POST', path: '/api/v1/documents',
        body: { targetKind: 'core.item', targetId: ITEM, role: 'insurance', date: '2026-09-01', warnDuplicate: 'true' } }]));
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
      await waitFor(() => expect(toasts()).toHaveTextContent('Document déposé.'));
      await waitFor(() => expect(api.gets()).toBeGreaterThan(before));
    });

    it('sends no date when none is given, and refuses one in the future', async () => {
      const api = serve();
      const { user } = await renderItem();
      await user.click(await screen.findByRole('button', { name: 'Déposer' }));
      const dialog = await screen.findByRole('dialog', { name: 'Déposer un document' });
      await user.upload(within(dialog).getByLabelText('Fichiers'), pdf());
      await user.type(within(dialog).getByRole('textbox', { name: 'Date du document' }), '01/01/2999');
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      expect(await within(dialog).findByText('La date ne peut pas être dans le futur.')).toBeInTheDocument();
      expect(api.writes).toEqual([]);

      await user.clear(within(dialog).getByRole('textbox', { name: 'Date du document' }));
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      await waitFor(() => expect(api.writes).toEqual([expect.objectContaining({ body: { targetKind: 'core.item', targetId: ITEM, role: 'statement', warnDuplicate: 'true' } })]));
    });

    it('warns of a file already in Nid: « Lier l’existant » links that document with the chosen type and uploads nothing', async () => {
      const existing = summary({ id: D3, title: 'Bail 2024', documentDate: '2024-09-01', archived: true, links: [link({ targetId: OTHER_ITEM, anchorItemId: OTHER_ITEM, label: 'Studio Lyon' })] });
      const api = serve({ upload: () => HttpResponse.json({ status: 409, code: 'documents.duplicate', title: 'Duplicate.', duplicate: existing }, { status: 409 }) });
      const { user } = await renderItem();
      const dialog = await open(user);
      await user.upload(within(dialog).getByLabelText('Fichiers'), pdf('bail.pdf'));
      await pick(user, 'Type', 'Bail');
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));

      const alert = await within(dialog).findByRole('alert');
      expect(alert).toHaveTextContent('bail.pdf est déjà dans Nid :');
      expect(alert).toHaveTextContent('« Bail 2024 » · 1 sept. 2024 · Archivé · lié à Studio Lyon');
      await user.click(within(alert).getByRole('button', { name: 'Lier l’existant' }));

      await waitFor(() => expect(api.writes.at(-1)).toEqual({ method: 'POST', path: `/api/v1/documents/${D3}/links`, body: { targetKind: 'core.item', targetId: ITEM, role: 'lease' } }));
      expect(api.sent).toEqual(['bail.pdf']);
      expect(await within(dialog).findByText('Déposé')).toBeInTheDocument();
    });

    it('« Ajouter quand même » uploads the file again without asking to be warned', async () => {
      const existing = summary({ id: D3, title: 'Bail 2024', links: [] });
      const api = serve({ upload: () => api.writes.length === 1
        ? HttpResponse.json({ status: 409, code: 'documents.duplicate', title: 'Duplicate.', duplicate: existing }, { status: 409 })
        : HttpResponse.json({ id: D2 }, { status: 201 }) });
      const { user } = await renderItem();
      const dialog = await open(user);
      await user.upload(within(dialog).getByLabelText('Fichiers'), pdf('bail.pdf'));
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      expect(await within(dialog).findByRole('alert')).toHaveTextContent('À classer');
      await user.click(within(dialog).getByRole('button', { name: 'Ajouter quand même' }));

      await waitFor(() => expect(api.writes.map((w) => w.body)).toEqual([
        { targetKind: 'core.item', targetId: ITEM, role: 'statement', warnDuplicate: 'true' },
        { targetKind: 'core.item', targetId: ITEM, role: 'statement' },
      ]));
      expect(await within(dialog).findByText('Déposé')).toBeInTheDocument();
    });

    it('words a refused file as the documents page does', async () => {
      serve({ upload: () => HttpResponse.json({ status: 415, code: 'documents.unsupported_type', title: 'Unsupported.' }, { status: 415 }) });
      const { user } = await renderItem();
      await user.click(await screen.findByRole('button', { name: 'Déposer' }));
      const dialog = await screen.findByRole('dialog', { name: 'Déposer un document' });
      await user.upload(within(dialog).getByLabelText('Fichiers'), pdf('notes.pdf'));
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      expect(await within(dialog).findByText('notes.pdf : ce type de fichier n’est pas pris en charge (PDF, PNG, JPEG ou e-mail .eml).')).toBeInTheDocument();
    });

    const open = async (user: ReturnType<typeof userEvent.setup>) => {
      await user.click(await screen.findByRole('button', { name: 'Déposer' }));
      return screen.findByRole('dialog', { name: 'Déposer un document' });
    };
    const refused = (name: string) => `${name} : ce type de fichier n’est pas pris en charge (PDF, PNG, JPEG ou e-mail .eml).`;

    it('links several picked files to the item with one type and no date', async () => {
      const api = serve();
      const { user } = await renderItem();
      const dialog = await open(user);
      await user.upload(within(dialog).getByLabelText('Fichiers'), [pdf('janvier.pdf'), pdf('fevrier.pdf')]);
      expect(within(dialog).queryByRole('textbox', { name: 'Date du document' })).not.toBeInTheDocument();
      await pick(user, 'Type', 'Assurance');
      await user.click(within(dialog).getByRole('button', { name: 'Déposer 2 fichiers' }));

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
      expect(api.sent).toEqual(['janvier.pdf', 'fevrier.pdf']);
      const body = { targetKind: 'core.item', targetId: ITEM, role: 'insurance', warnDuplicate: 'true' };
      expect(api.writes.map((w) => w.body)).toEqual([body, body]);
      await waitFor(() => expect(toasts()).toHaveTextContent('2 documents déposés.'));
    });

    it('adds dropped files to those already chosen, and one can be removed before sending', async () => {
      const api = serve();
      const { user } = await renderItem();
      const dialog = await open(user);
      const zone = within(dialog).getByText('Glissez vos fichiers ici').closest('[data-drop-zone]')!;
      fireEvent.drop(zone, { dataTransfer: { files: [pdf('bail.pdf'), pdf('avis.pdf')] } });
      fireEvent.drop(zone, { dataTransfer: { files: [pdf('edl.pdf')] } });
      expect(await within(dialog).findByText('edl.pdf')).toBeInTheDocument();
      await user.click(within(dialog).getByRole('button', { name: 'Retirer avis.pdf' }));
      expect(within(dialog).queryByText('avis.pdf')).not.toBeInTheDocument();
      await user.click(within(dialog).getByRole('button', { name: 'Déposer 2 fichiers' }));
      await waitFor(() => expect(api.sent).toEqual(['bail.pdf', 'edl.pdf']));
    });

    it('offers the date again once a single file is left', async () => {
      serve();
      const { user } = await renderItem();
      const dialog = await open(user);
      await user.upload(within(dialog).getByLabelText('Fichiers'), [pdf('bail.pdf'), pdf('avis.pdf')]);
      await user.click(within(dialog).getByRole('button', { name: 'Retirer avis.pdf' }));
      expect(within(dialog).getByRole('textbox', { name: 'Date du document' })).toBeInTheDocument();
    });

    it('refuses a file of another type without sending it, and the others stay', async () => {
      const api = serve();
      const { user } = await renderItem();
      const dialog = await open(user);
      const zone = within(dialog).getByText('Glissez vos fichiers ici').closest('[data-drop-zone]')!;

      fireEvent.drop(zone, { dataTransfer: { files: [new File(['hello'], 'notes.txt', { type: 'text/plain' })] } });
      expect(await within(dialog).findByText(refused('notes.txt'))).toBeInTheDocument();
      expect(within(dialog).getByRole('button', { name: 'Déposer' })).toBeDisabled();

      fireEvent.drop(zone, { dataTransfer: { files: [pdf('bail.pdf'), new File(['x'], 'image.gif', { type: 'image/gif' }), pdf('avis.pdf')] } });
      expect(await within(dialog).findByText(refused('image.gif'))).toBeInTheDocument();
      // A refused file does not count: one good file left keeps its date.
      await user.click(within(dialog).getByRole('button', { name: 'Retirer avis.pdf' }));
      expect(within(dialog).getByRole('textbox', { name: 'Date du document' })).toBeInTheDocument();
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      await waitFor(() => expect(api.sent).toEqual(['bail.pdf']));
    });

    it('says which file failed, keeps the dialog open, and sends only that one again', async () => {
      let broken = true;
      const api = serve({ upload: (name) => (name === 'b.pdf' && broken
        ? HttpResponse.json({ status: 413, code: 'documents.too_large', title: 'Too large.' }, { status: 413 })
        : HttpResponse.json({ id: D2 }, { status: 201 })) });
      const { user } = await renderItem();
      const dialog = await open(user);
      await user.upload(within(dialog).getByLabelText('Fichiers'), [pdf('a.pdf'), pdf('b.pdf'), pdf('c.pdf')]);
      const before = api.gets();
      await user.click(within(dialog).getByRole('button', { name: 'Déposer 3 fichiers' }));

      expect(await within(dialog).findByRole('alert')).toHaveTextContent('b.pdf');
      expect(api.sent).toEqual(['a.pdf', 'b.pdf', 'c.pdf']);
      await waitFor(() => expect(api.gets()).toBeGreaterThan(before));
      expect(within(dialog).queryByRole('button', { name: 'Retirer a.pdf' })).not.toBeInTheDocument();
      expect(within(dialog).getByRole('button', { name: 'Fermer' })).toBeInTheDocument();

      broken = false;
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
      expect(api.sent).toEqual(['a.pdf', 'b.pdf', 'c.pdf', 'b.pdf']);
      await waitFor(() => expect(toasts()).toHaveTextContent('3 documents déposés.'));
    });

    it('keeps the type and the buttons out of the list that scrolls, whatever the number of files', async () => {
      serve();
      const { user } = await renderItem();
      const dialog = await open(user);
      const long = 'Avis d’opération (investissement progressif) - Vita 1234567';
      await user.upload(within(dialog).getByLabelText('Fichiers'), Array.from({ length: 30 }, (_, i) => pdf(`${long} ${i}.pdf`)));

      const list = within(dialog).getByRole('list');
      expect(list).toHaveClass('overflow-y-auto');
      expect(within(list).getAllByRole('listitem')).toHaveLength(30);
      expect(within(list).getByText(`${long} 7.pdf`)).toHaveAttribute('title', `${long} 7.pdf`);
      expect(within(list).getByText(`${long} 7.pdf`)).toHaveClass('truncate');
      expect(list).not.toContainElement(within(dialog).getByRole('combobox', { name: 'Type' }));
      const send = within(dialog).getByRole('button', { name: 'Déposer 30 fichiers' });
      expect(send.parentElement).toHaveClass('shrink-0');
      expect(send.parentElement?.parentElement).toHaveClass('min-h-0');
    });

    it('folds the drop zone to the count and total size, and clears the list', async () => {
      const api = serve();
      const { user } = await renderItem();
      const dialog = await open(user);
      expect(within(dialog).getByText('Glissez vos fichiers ici')).toBeInTheDocument();
      expect(within(dialog).queryByRole('button', { name: 'Tout retirer' })).not.toBeInTheDocument();
      const input = within(dialog).getByLabelText('Fichiers');
      await user.upload(input, new File(['x'.repeat(2048)], 'bail.pdf', { type: 'application/pdf' }));
      expect(within(dialog).getByText('1 fichier · 2 Ko')).toBeInTheDocument();
      await user.upload(within(dialog).getByLabelText('Fichiers'), new File(['x'.repeat(2048)], 'avis.pdf', { type: 'application/pdf' }));
      expect(within(dialog).getByText('2 fichiers · 4 Ko')).toBeInTheDocument();
      // The same input: the focus stays on it as the zone folds.
      expect(within(dialog).getByLabelText('Fichiers')).toBe(input);

      await user.click(within(dialog).getByRole('button', { name: 'Tout retirer' }));
      expect(within(dialog).queryByRole('list')).not.toBeInTheDocument();
      expect(within(dialog).getByText('Glissez vos fichiers ici')).toBeInTheDocument();
      expect(within(dialog).getByRole('button', { name: 'Déposer' })).toBeDisabled();
      expect(api.sent).toEqual([]);
    });

    it('keeps a sent file when the list is cleared after a failure', async () => {
      const api = serve({ upload: (name) => (name === 'b.pdf'
        ? HttpResponse.json({ status: 413, code: 'documents.too_large', title: 'Too large.' }, { status: 413 })
        : HttpResponse.json({ id: D2 }, { status: 201 })) });
      const { user } = await renderItem();
      const dialog = await open(user);
      await user.upload(within(dialog).getByLabelText('Fichiers'), [pdf('a.pdf'), pdf('b.pdf')]);
      await user.click(within(dialog).getByRole('button', { name: 'Déposer 2 fichiers' }));
      await within(dialog).findByRole('alert');

      await user.click(within(dialog).getByRole('button', { name: 'Tout retirer' }));
      expect(within(dialog).getByText('a.pdf')).toBeInTheDocument();
      expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument();
      expect(within(dialog).getByRole('button', { name: 'Fermer' })).toBeInTheDocument();
      expect(api.sent).toEqual(['a.pdf', 'b.pdf']);
    });

    it('passes the axe check with files listed', async () => {
      serve();
      const { user } = await renderItem();
      const dialog = await open(user);
      await user.upload(within(dialog).getByLabelText('Fichiers'), [pdf('bail.pdf'), pdf('avis.pdf')]);
      await expectNoAxeViolations(dialog);
    });
  });

  describe('Relier un document', () => {
    const mine = [
      summary({ id: D2, title: 'Avenant', links: [] }),
      summary({ id: D3, title: 'Lu seulement', canEdit: false, links: [] }),
      summary({ id: D4, title: 'Archivé', archived: true, links: [] }),
      d1(),
      summary({ id: D5, title: 'Bulletin octobre', links: [phaseLink()] }),
      summary({ id: id(7), title: 'Autre fiche', links: [link({ targetId: OTHER_ITEM })] }),
    ];

    async function choose(user: ReturnType<typeof userEvent.setup>, title = 'Avenant') {
      await user.click(await screen.findByRole('button', { name: 'Relier un document' }));
      await screen.findByRole('dialog', { name: 'Relier un document' });
      await pick(user, 'Document', title);
      await user.click(screen.getByRole('button', { name: 'Relier' }));
    }

    it('offers the editable, unarchived documents not yet linked to the item, a phase link aside', async () => {
      serve({ mine });
      const { user } = await renderItem();
      await user.click(await screen.findByRole('button', { name: 'Relier un document' }));
      await screen.findByRole('dialog', { name: 'Relier un document' });
      await user.click(screen.getByRole('combobox', { name: 'Document' }));
      const options = within(await screen.findByRole('listbox')).getAllByRole('option').map((o) => o.textContent);
      expect(options).toEqual(['Avenant', 'Bulletin octobre', 'Autre fiche']);
    });

    it('links at once when nobody gains the document', async () => {
      const api = serve({ mine });
      const { user } = await renderItem();
      await choose(user);
      await waitFor(() => expect(api.writes).toEqual([{ method: 'POST', path: `/api/v1/documents/${D2}/links`, body: { targetKind: 'core.item', targetId: ITEM, role: 'statement' } }]));
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
      await waitFor(() => expect(toasts()).toHaveTextContent('Document relié.'));
    });

    it('names who would gain it, and links only once confirmed', async () => {
      const api = serve({ mine, preview: [{ id: 'b', name: 'Bob' }] });
      const { user } = await renderItem();
      await choose(user);
      expect(await screen.findByText('Bob pourra lire ce document.')).toBeInTheDocument();
      expect(api.writes).toEqual([]);
      await user.click(screen.getByRole('button', { name: 'Relier quand même' }));
      await waitFor(() => expect(api.writes).toEqual([expect.objectContaining({ method: 'POST', path: `/api/v1/documents/${D2}/links` })]));
    });
  });

  it('Retirer unlinks that row’s link after a confirmation, and the list reloads', async () => {
    const api = serve();
    const { user } = await renderItem();
    const menu = await openMenu(user, 'Bulletin 09/2026');
    await user.click(within(menu).getByRole('menuitem', { name: 'Retirer' }));
    expect(api.writes).toEqual([]);
    expect(await screen.findByText('Retirer ce lien ? Le document reste dans Documents.')).toBeInTheDocument();
    const before = api.gets();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(api.writes).toEqual([{ method: 'DELETE', path: `/api/v1/documents/${D1}/links/income.phase/${P1}`, body: undefined }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Lien retiré.'));
    await waitFor(() => expect(api.gets()).toBeGreaterThan(before));
  });

  it.each([
    ['a document the actor cannot edit', d1({ canEdit: false })],
    ['a link the actor cannot write', d1({ links: [link({ role: 'payslip' }), phaseLink({ canWrite: false })] })],
  ])('offers no Retirer on %s', async (_, doc) => {
    serve({ anchored: [doc] });
    const { user } = await renderItem();
    const menu = await openMenu(user, 'Bulletin 09/2026');
    expect(within(menu).getByRole('menuitem', { name: 'Télécharger' })).toBeInTheDocument();
    expect(within(menu).queryByRole('menuitem', { name: 'Retirer' })).not.toBeInTheDocument();
  });

  it('passes the axe check', async () => {
    serve();
    const { container } = await renderItem();
    await table();
    await expectNoAxeViolations(container);
  });
});
