import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { expectLabelBefore } from '@/test/filters';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { stubLocalDay } from '@/test/timeZone';
import type { DocumentList } from '@/api/documents';
import { ALICE, DOC, ITEM, link, list, summary } from './testData';

/** As the server (issue #420): the facets of every row, the rows the filters keep, one page of `size`. */
function narrow(body: DocumentList, p: URLSearchParams): DocumentList {
  const role = p.get('role'), year = p.get('year'), linked = p.get('linked'), q = p.get('q')?.toLowerCase();
  const kept = body.items.filter((d) => (!role || d.links.some((l) => l.role === role))
    && (!year || (d.documentDate ?? d.uploadedAt).startsWith(year))
    && (!linked || d.links.some((l) => l.targetId === linked))
    && (!q || d.title.toLowerCase().includes(q)));
  const page = Number(p.get('page') ?? 1);
  return { ...body, items: kept.slice((page - 1) * 50, page * 50), total: kept.length };
}

function serve(body: DocumentList, seen: string[] = []) {
  server.use(http.get('*/api/v1/documents', ({ request }) => {
    const url = new URL(request.url);
    seen.push(url.search);
    return HttpResponse.json(narrow(body, url.searchParams));
  }));
}

const titles = async () => (await rows()).map((r) => r.querySelector('td')?.textContent);

async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des documents' });
  return within(table).getAllByRole('row').slice(1);
}

const three = () => list([
  summary({ id: 'a', title: 'Bail 2024', documentDate: '2024-03-01', links: [link({ role: 'lease', label: 'Appartement Lyon' })] }),
  summary({ id: 'b', title: 'Bulletin septembre', documentDate: null, uploadedAt: '2026-10-02T08:00:00Z',
    links: [link({ targetKind: 'core.person', targetId: '0192f0aa-0000-7000-8000-00000000f001', role: 'payslip', label: 'Alice' })] }),
  summary({ id: 'c', title: 'Facture box', documentDate: '2025-06-10', sizeBytes: 1_572_864,
    links: [link({ targetId: '0192f0aa-0000-7000-8000-00000000e002', role: 'invoice', label: 'Box internet' }), link({ role: 'other', label: 'Appartement Lyon' })] }),
]);

describe('DocumentsPage', () => {
  beforeAll(async () => { await import('./DocumentsPage'); }, 30_000);

  it('lists the documents with their types, linked elements, date and size', async () => {
    const seen: string[] = [];
    serve(three(), seen);
    const { container } = await renderApp('/documents');

    const items = await rows();
    expect(items).toHaveLength(3);
    expect(items[0]).toHaveTextContent('Bail 2024');
    expect(items[0]).toHaveTextContent('Bail');
    expect(items[0]).toHaveTextContent('Appartement Lyon');
    expect(items[0]).toHaveTextContent('1 mars 2024');
    expect(items[0]).toHaveTextContent('245 Ko');
    expect(items[1]).toHaveTextContent('Bulletin de paie');
    expect(items[1]).toHaveTextContent('2 oct. 2026');
    expect(items[2]).toHaveTextContent('Facture');
    expect(items[2]).toHaveTextContent('Box internet');
    expect(items[2]).toHaveTextContent('1,5 Mo');
    expect(seen[0]).toContain('scope=household');
    expect(seen[0]).toContain('inbox=false');
    await expectNoAxeViolations(container);
  });

  it('reads like the other lists: singular headers, the owner, « Liés à » cut with the whole list in a tooltip (issue 392)', async () => {
    serve(three());
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: ALICE, kind: 'adult', displayName: 'Alice Martin', email: null }])));
    const { user } = await renderApp('/documents');

    const table = await screen.findByRole('table', { name: 'Liste des documents' });
    expect(within(table).getByRole('columnheader', { name: /^Type/ })).toBeInTheDocument();
    expect(within(table).getByRole('columnheader', { name: /^Propriétaire/ })).toBeInTheDocument();
    const facture = (await rows())[2]!;
    expect(await within(facture).findByText('Alice Martin')).toBeInTheDocument();
    const linked = within(facture).getByText('Box internet, Appartement Lyon');
    expect(linked).toHaveClass('truncate');
    await user.hover(linked);
    const tip = await screen.findByRole('tooltip');
    expect(within(tip).getByText('Box internet')).toBeInTheDocument();
    expect(within(tip).getByText('Appartement Lyon')).toBeInTheDocument();
  });

  it('the « À classer » tab asks for the inbox and puts it in the URL', async () => {
    const seen: string[] = [];
    serve(list([summary({ links: [] })]), seen);
    const { user, router } = await renderApp('/documents');
    await rows();
    await user.click(screen.getByRole('tab', { name: 'À classer' }));

    await waitFor(() => expect(seen.some((s) => s.includes('inbox=true'))).toBe(true));
    expect(router.state.location.search).toEqual({ tab: 'inbox' });
  });

  it('asks in the scope of the selector', async () => {
    const seen: string[] = [];
    serve(three(), seen);
    await renderApp('/documents?scope=me');
    await rows();
    expect(seen[0]).toContain('scope=me');
  });

  it('asks the server for the type, year, linked element and title, and keeps them in the URL (issue #420)', async () => {
    const seen: string[] = [];
    serve(three(), seen);
    const { user, router } = await renderApp('/documents');
    await rows();
    expect(seen[0]).toContain('page=1');

    await pick(user, 'Type', 'Bulletin de paie');
    await waitFor(async () => expect(await titles()).toEqual(['Bulletin septembre']));
    expect(seen.at(-1)).toContain('role=payslip');
    expect(router.state.location.search).toEqual({ type: 'payslip' });
    await pick(user, 'Type', 'Tous les types');

    // No document date: the upload year counts, on the server.
    await pick(user, 'Année', '2026');
    await waitFor(async () => expect(await titles()).toEqual(['Bulletin septembre']));
    expect(seen.at(-1)).toContain('year=2026');
    await pick(user, 'Année', 'Toutes les années');

    await pick(user, 'Élément lié', 'Appartement Lyon');
    await waitFor(async () => expect(await titles()).toEqual(['Bail 2024', 'Facture box']));
    expect(seen.at(-1)).toContain(`linked=${ITEM}`);

    await user.type(screen.getByRole('searchbox', { name: 'Rechercher' }), 'fact');
    await waitFor(async () => expect(await titles()).toEqual(['Facture box']));
    expect(seen.at(-1)).toContain('q=fact');
    expect(router.state.location.search).toEqual({ item: ITEM, q: 'fact' });
    expect(screen.getByText('1 document')).toBeInTheDocument();
  });

  it('reads the filters and the page from the URL', async () => {
    const seen: string[] = [];
    serve(three(), seen);
    await renderApp('/documents?type=payslip&year=2026&q=bul&page=2');
    await waitFor(() => expect(seen.length).toBeGreaterThan(0));
    for (const part of ['role=payslip', 'year=2026', 'q=bul', 'page=2']) expect(seen[0]).toContain(part);
  });

  it('shows the total and turns the pages, one page of 50 at a time', async () => {
    const seen: string[] = [];
    serve(list(Array.from({ length: 120 }, (_, i) => summary({ id: `p${i}`, title: `Pièce ${i}` }))), seen);
    const { user, router } = await renderApp('/documents?page=3');
    const pages = await screen.findByRole('navigation', { name: 'Pages des documents' });
    expect(pages).toHaveTextContent('Page 3 sur 3');
    expect(screen.getByText('120 documents')).toBeInTheDocument();
    expect(await rows()).toHaveLength(20);
    expect(within(pages).getByRole('button', { name: 'Suivant' })).toBeDisabled();

    await user.click(within(pages).getByRole('button', { name: 'Précédent' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ page: 2 }));
    expect(seen.at(-1)).toContain('page=2');
    await user.click(within(pages).getByRole('button', { name: 'Précédent' }));
    await waitFor(() => expect(router.state.location.search).toEqual({}));
    await waitFor(() => expect(within(pages).getByRole('button', { name: 'Précédent' })).toBeDisabled());
  });

  it('a page past the end steps back to the last one', async () => {
    serve(three());
    const { router } = await renderApp('/documents?page=4');
    await waitFor(() => expect(router.state.location.search).toEqual({}));
    expect(await rows()).toHaveLength(3);
  });

  it('« À classer »: a new page clears the selection', async () => {
    serve(list(Array.from({ length: 60 }, (_, i) => summary({ id: `i${i}`, title: `Scan ${i}`, links: [] }))));
    const { user } = await renderApp('/documents?tab=inbox');
    await rows();
    await user.click(within(await screen.findByRole('table', { name: 'Liste des documents' })).getByRole('checkbox', { name: 'Sélectionner Scan 0' }));
    expect(screen.getByText('1 document sélectionné')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Suivant' }));
    expect((await screen.findAllByRole('link', { name: 'Scan 50' })).length).toBeGreaterThan(0);
    expect(screen.queryByText(/sélectionné/)).not.toBeInTheDocument();
  });

  it('the document filters sit in one row, each labelled before its control', async () => {
    serve(three());
    await renderApp('/documents');
    await rows();
    const row = screen.getByRole('group', { name: 'Filtres' });
    expectLabelBefore(within(row).getByText('Rechercher'), within(row).getByRole('searchbox', { name: 'Rechercher' }));
    for (const name of ['Type', 'Année', 'Élément lié']) {
      expectLabelBefore(within(row).getByText(name), within(row).getByRole('combobox', { name }));
    }
    expect(within(row).getByRole('switch', { name: 'Afficher les archivés' })).toBeInTheDocument();
  });

  it('« Afficher les archivés » asks for the archived documents', async () => {
    const seen: string[] = [];
    serve(three(), seen);
    const { user } = await renderApp('/documents');
    await rows();
    await user.click(screen.getByRole('switch', { name: 'Afficher les archivés' }));
    await waitFor(() => expect(seen.some((s) => s.includes('archived=true'))).toBe(true));
  });

  it('the row menu downloads, edits and archives', async () => {
    serve(list([summary()]));
    const posts: string[] = [];
    server.use(http.post('*/api/v1/documents/:id/archive', ({ params }) => { posts.push(String(params.id)); return new HttpResponse(null, { status: 204 }); }));
    const { user } = await renderApp('/documents');
    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Bail 2024' })[0]!);

    expect(await screen.findByRole('menuitem', { name: 'Télécharger' })).toHaveAttribute('href', `/api/v1/documents/${DOC}/content`);
    expect(screen.getByRole('menuitem', { name: 'Modifier' })).toBeInTheDocument();
    await user.click(screen.getByRole('menuitem', { name: 'Archiver' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(posts).toEqual([DOC]));
  });

  it('sends the focus to the tab list when the archived row disappears', async () => {
    let body = list([summary()]);
    server.use(
      http.get('*/api/v1/documents', () => HttpResponse.json(body)),
      http.post('*/api/v1/documents/:id/archive', () => { body = list([]); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp('/documents');
    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Bail 2024' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Archiver' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(screen.getByRole('tab', { name: 'Tous' })).toHaveFocus());
  });

  it('shows and filters the upload day in the browser time zone', async () => {
    stubLocalDay(13); // Auckland: 2026-10-26T23:30Z is already the 27th there
    serve(list([summary({ documentDate: null, uploadedAt: '2026-10-26T23:30:00Z' })]));
    await renderApp('/documents');
    expect((await rows())[0]).toHaveTextContent('27 oct. 2026');
  });

  it('offers only the download on a document the reader cannot edit', async () => {
    serve(list([summary({ canEdit: false })]));
    const { user } = await renderApp('/documents');
    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Bail 2024' })[0]!);
    expect(await screen.findByRole('menuitem', { name: 'Télécharger' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Archiver' })).not.toBeInTheDocument();
  });

  it('the title opens the document, and « Modifier » goes there too', async () => {
    serve(list([summary()]));
    const { user, router } = await renderApp('/documents');
    const [row] = await rows();
    expect(within(row!).getByRole('link', { name: 'Bail 2024' })).toHaveAttribute('href', `/documents/${DOC}`);
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Bail 2024' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Modifier' }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/documents/${DOC}`));
  });

  it('clears a year or element filter the new rows no longer offer', async () => {
    let body = three();
    server.use(http.get('*/api/v1/documents', ({ request }) => HttpResponse.json(narrow(body, new URL(request.url).searchParams))));
    const { user } = await renderApp('/documents');
    await rows();
    await pick(user, 'Année', '2026');
    await pick(user, 'Élément lié', 'Alice');
    await waitFor(async () => expect(await rows()).toHaveLength(1));

    body = list([summary({ id: 'a', title: 'Bail 2024' }), summary({ id: 'z', title: 'Avis 2025', documentDate: '2025-09-01', links: [] })]);
    await user.click(screen.getByRole('switch', { name: 'Afficher les archivés' }));

    await waitFor(async () => expect((await rows()).map((r) => r.querySelector('td')?.textContent)).toEqual(['Bail 2024', 'Avis 2025']));
    expect(screen.getByRole('combobox', { name: 'Année' })).toHaveTextContent('Toutes les années');
    expect(screen.getByRole('combobox', { name: 'Élément lié' })).toHaveTextContent('Tous les éléments');
  });

  it('invites to drop the first document', async () => {
    serve(list([]));
    await renderApp('/documents');
    expect(await screen.findByRole('heading', { name: 'Aucun document' })).toBeInTheDocument();
  });
});
