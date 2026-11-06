import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { expectLabelBefore } from '@/test/filters';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { stubLocalDay } from '@/test/timeZone';
import type { DocumentList } from './api';
import { ALICE, DOC, link, list, summary } from './testData';

function serve(body: DocumentList, seen: string[] = []) {
  server.use(http.get('*/api/v1/documents', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
}

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

  it('filters by type, year and linked element without asking again', async () => {
    const seen: string[] = [];
    serve(three(), seen);
    const { user } = await renderApp('/documents');
    await rows();
    const asked = seen.length;

    await pick(user, 'Type', 'Bulletin de paie');
    await waitFor(async () => expect((await rows()).map((r) => r.querySelector('td')?.textContent)).toEqual(['Bulletin septembre']));
    await pick(user, 'Type', 'Tous les types');

    // No document date: the upload year counts.
    await pick(user, 'Année', '2026');
    await waitFor(async () => expect((await rows()).map((r) => r.querySelector('td')?.textContent)).toEqual(['Bulletin septembre']));
    await pick(user, 'Année', 'Toutes les années');

    await pick(user, 'Élément lié', 'Appartement Lyon');
    await waitFor(async () => expect((await rows()).map((r) => r.querySelector('td')?.textContent)).toEqual(['Bail 2024', 'Facture box']));

    expect(seen).toHaveLength(asked);
  });

  it('the document filters sit in one row, each labelled before its control', async () => {
    serve(three());
    await renderApp('/documents');
    await rows();
    const row = screen.getByRole('group', { name: 'Filtres' });
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
    server.use(http.get('*/api/v1/documents', () => HttpResponse.json(body)));
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
