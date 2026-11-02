import { File as NodeFile } from 'node:buffer';
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { createApi } from '@/api/client';
import { createQueryClient } from '@/app/bootstrap';
import { Providers } from '@/app/Providers';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { aliceSession, allCapabilities } from '@/test/fixtures';
import { server } from '@/test/server';
import type { DocumentSummary } from './api';
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
function serve({ anchored = [d1(), older], mine = [] as DocumentSummary[], preview = [] as { id: string; name: string }[], upload = () => HttpResponse.json({ id: D2 }, { status: 201 }) } = {}) {
  let gets = 0;
  const writes: { method: string; path: string; body: unknown }[] = [];
  server.use(
    http.get('*/api/v1/documents', ({ request }) => {
      if (new URL(request.url).searchParams.get('anchor') === ITEM) { gets += 1; return HttpResponse.json(list(anchored)); }
      return HttpResponse.json(list(mine));
    }),
    http.post('*/api/v1/documents', async ({ request }) => {
      writes.push({ method: 'POST', path: '/api/v1/documents', body: Object.fromEntries([...(await request.formData()).entries()].filter(([k]) => k !== 'file')) });
      return upload();
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
  return { writes, gets: () => gets };
}

/** The component alone under the real providers; its title links need a router. */
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
      await user.upload(within(dialog).getByLabelText('Fichier'), pdf());
      await pick(user, 'Type', 'Assurance');
      await user.type(within(dialog).getByRole('textbox', { name: 'Date du document' }), '01/09/2026');
      const before = api.gets();
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));

      await waitFor(() => expect(api.writes).toEqual([{ method: 'POST', path: '/api/v1/documents',
        body: { targetKind: 'core.item', targetId: ITEM, role: 'insurance', date: '2026-09-01' } }]));
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
      expect(screen.getByText('Document déposé.')).toBeInTheDocument();
      await waitFor(() => expect(api.gets()).toBeGreaterThan(before));
    });

    it('sends no date when none is given, and refuses one in the future', async () => {
      const api = serve();
      const { user } = await renderItem();
      await user.click(await screen.findByRole('button', { name: 'Déposer' }));
      const dialog = await screen.findByRole('dialog', { name: 'Déposer un document' });
      await user.upload(within(dialog).getByLabelText('Fichier'), pdf());
      await user.type(within(dialog).getByRole('textbox', { name: 'Date du document' }), '01/01/2999');
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      expect(await within(dialog).findByText('La date ne peut pas être dans le futur.')).toBeInTheDocument();
      expect(api.writes).toEqual([]);

      await user.clear(within(dialog).getByRole('textbox', { name: 'Date du document' }));
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      await waitFor(() => expect(api.writes).toEqual([expect.objectContaining({ body: { targetKind: 'core.item', targetId: ITEM, role: 'statement' } })]));
    });

    it('words a refused file as the documents page does', async () => {
      serve({ upload: () => HttpResponse.json({ status: 415, code: 'documents.unsupported_type', title: 'Unsupported.' }, { status: 415 }) });
      const { user } = await renderItem();
      await user.click(await screen.findByRole('button', { name: 'Déposer' }));
      const dialog = await screen.findByRole('dialog', { name: 'Déposer un document' });
      await user.upload(within(dialog).getByLabelText('Fichier'), pdf('notes.pdf'));
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      expect(await within(dialog).findByText('notes.pdf : ce type de fichier n’est pas pris en charge (PDF, PNG, JPEG ou e-mail .eml).')).toBeInTheDocument();
    });

    it('takes the first dropped file, and refuses one of another type without sending it', async () => {
      const api = serve();
      const { user } = await renderItem();
      await user.click(await screen.findByRole('button', { name: 'Déposer' }));
      const dialog = await screen.findByRole('dialog', { name: 'Déposer un document' });
      const zone = within(dialog).getByText('Glissez un fichier ici').closest('[data-drop-zone]')!;

      fireEvent.drop(zone, { dataTransfer: { files: [new File(['hello'], 'notes.txt', { type: 'text/plain' })] } });
      expect(await within(dialog).findByText('notes.txt : ce type de fichier n’est pas pris en charge (PDF, PNG, JPEG ou e-mail .eml).')).toBeInTheDocument();
      expect(within(dialog).getByRole('button', { name: 'Déposer' })).toBeDisabled();

      fireEvent.drop(zone, { dataTransfer: { files: [pdf('bail.pdf'), pdf('avis.pdf')] } });
      expect(await within(dialog).findByText('bail.pdf')).toBeInTheDocument();
      expect(within(dialog).queryByText('avis.pdf')).not.toBeInTheDocument();
      await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
      await waitFor(() => expect(api.writes).toHaveLength(1));
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
      expect(screen.getByText('Document relié.')).toBeInTheDocument();
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
    expect(await screen.findByText('Lien retiré.')).toBeInTheDocument();
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
