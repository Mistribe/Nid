import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import type { DocumentSummary } from './api';
import { list, summary, targets } from './testData';

const BOX = '0192f0aa-0000-7000-8000-00000000e002';
const problem = (status: number, code: string) =>
  HttpResponse.json({ status, code, title: code }, { status, headers: { 'content-type': 'application/problem+json' } });

/**
 * Three unlinked documents behind « À classer »; a linked one leaves the inbox. `gained` answers each preview, `refuse`
 * refuses one document's link.
 */
function serveInbox(over: { gained?: Record<string, { id: string; name: string }[]>; refuse?: Record<string, Response> } = {}) {
  let inbox: DocumentSummary[] = [
    summary({ id: 'a', title: 'Bulletin janvier', documentDate: '2026-01-31', links: [] }),
    summary({ id: 'b', title: 'Bulletin février', documentDate: '2026-02-28', links: [] }),
    summary({ id: 'c', title: 'Relevé 2025', documentDate: '2025-12-31', links: [] }),
  ];
  const previews: string[] = [];
  const posts: { id: string; body: unknown }[] = [];
  server.use(
    http.get('*/api/v1/documents/targets', () => HttpResponse.json(targets())),
    // The server filters by year (issue #420); the facets stay those of the whole inbox.
    http.get('*/api/v1/documents', ({ request }) => {
      const year = new URL(request.url).searchParams.get('year');
      const shown = inbox.filter((d) => !year || d.documentDate?.startsWith(year));
      return HttpResponse.json({ ...list(inbox), items: shown, total: shown.length });
    }),
    http.get('*/api/v1/documents/:id/links/preview', ({ params }) => {
      previews.push(String(params.id));
      return HttpResponse.json({ gainedReaders: over.gained?.[String(params.id)] ?? [] });
    }),
    http.post('*/api/v1/documents/:id/links', async ({ request, params }) => {
      const id = String(params.id);
      posts.push({ id, body: await request.json() });
      const refusal = over.refuse?.[id];
      if (refusal) return refusal.clone();
      inbox = inbox.filter((d) => d.id !== id);
      return HttpResponse.json({ gainedReaders: [] });
    }),
  );
  return { previews, posts, ids: () => posts.map((p) => p.id).sort() };
}

const table = () => screen.findByRole('table', { name: 'Liste des documents' });
const titles = async () => within(await table()).getAllByRole('row').slice(1).map((r) => within(r).getByRole('link').textContent);

async function select(user: Awaited<ReturnType<typeof renderApp>>['user'], ...names: string[]) {
  for (const name of names) await user.click(within(await table()).getByRole('checkbox', { name: `Sélectionner ${name}` }));
}

async function file(user: Awaited<ReturnType<typeof renderApp>>['user'], target = 'Box internet', role = 'Bulletin de paie', count = 2) {
  await user.click(screen.getByRole('button', { name: 'Classer' }));
  const dialog = await screen.findByRole('dialog', { name: `Classer ${count} documents` });
  await pick(user, 'Élément', target);
  await pick(user, 'Rôle', role);
  await user.click(within(dialog).getByRole('button', { name: 'Classer' }));
}

describe('FileDocumentsDialog', () => {
  beforeAll(async () => { await import('./DocumentsPage'); await import('./FileDocumentsDialog'); }, 30_000);

  it('selects rows one by one or all at once, on « À classer » only', async () => {
    serveInbox();
    const { user, container } = await renderApp('/documents?tab=inbox');
    await select(user, 'Bulletin janvier');
    expect(screen.getByText('1 document sélectionné')).toBeInTheDocument();
    const all = within(await table()).getByRole('checkbox', { name: 'Tout sélectionner' });
    expect(all).toHaveAttribute('aria-checked', 'mixed');

    await user.click(all);
    expect(screen.getByText('3 documents sélectionnés')).toBeInTheDocument();
    await user.click(all);
    expect(screen.queryByRole('button', { name: 'Classer' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('tab', { name: 'Tous' }));
    await table();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  it('clears the selection when a filter or the tab changes', async () => {
    serveInbox();
    const { user } = await renderApp('/documents?tab=inbox');
    await select(user, 'Bulletin janvier', 'Relevé 2025');
    await pick(user, 'Année', '2026');
    await waitFor(async () => expect(await titles()).toEqual(['Bulletin janvier', 'Bulletin février']));
    expect(within(await table()).getByRole('checkbox', { name: 'Sélectionner Bulletin janvier' })).toHaveAttribute('aria-checked', 'false');
    expect(screen.queryByRole('button', { name: 'Classer' })).not.toBeInTheDocument();

    await select(user, 'Bulletin février');
    await user.click(screen.getByRole('tab', { name: 'Tous' }));
    await user.click(screen.getByRole('tab', { name: 'À classer' }));
    await table();
    expect(screen.queryByRole('button', { name: 'Classer' })).not.toBeInTheDocument();
  });

  it('files every selected document under the same element and type, and they leave « À classer »', async () => {
    const sent = serveInbox();
    const { user } = await renderApp('/documents?tab=inbox');
    await select(user, 'Bulletin janvier', 'Bulletin février');
    await file(user);

    await waitFor(() => expect(sent.ids()).toEqual(['a', 'b']));
    expect(sent.posts.map((p) => p.body)).toEqual([
      { targetKind: 'core.item', targetId: BOX, role: 'payslip' }, { targetKind: 'core.item', targetId: BOX, role: 'payslip' }]);
    expect(sent.previews.sort()).toEqual(['a', 'b']);
    await waitFor(async () => expect(await titles()).toEqual(['Relevé 2025']));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(toasts()).toHaveTextContent('2 documents classés.');
    expect(screen.queryByRole('button', { name: 'Classer' })).not.toBeInTheDocument();
  });

  it('names the readers gained across the selection and writes nothing until confirmed', async () => {
    const sent = serveInbox({ gained: { a: [{ id: 'b', name: 'Bob' }], b: [{ id: 'b', name: 'Bob' }, { id: 'c', name: 'Chloé' }] } });
    const { user } = await renderApp('/documents?tab=inbox');
    await select(user, 'Bulletin janvier', 'Bulletin février');
    await file(user);

    expect(await screen.findByText('Bob et Chloé pourront lire ces documents.')).toBeInTheDocument();
    expect(sent.posts).toEqual([]);
    await user.click(screen.getByRole('button', { name: 'Classer quand même' }));
    await waitFor(() => expect(sent.ids()).toEqual(['a', 'b']));
  });

  it('posts nothing when the widening is cancelled', async () => {
    const sent = serveInbox({ gained: { a: [{ id: 'b', name: 'Bob' }] } });
    const { user } = await renderApp('/documents?tab=inbox');
    await select(user, 'Bulletin janvier', 'Bulletin février');
    await file(user);
    await screen.findByText('Bob pourra lire ces documents.');
    await user.click(screen.getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent.posts).toEqual([]);
    expect(screen.getByText('2 documents sélectionnés')).toBeInTheDocument();
  });

  it('files the others when the server refuses one, which stays selected with its error', async () => {
    const sent = serveInbox({ refuse: { b: problem(403, 'documents.forbidden') } });
    const { user } = await renderApp('/documents?tab=inbox');
    await select(user, 'Bulletin janvier', 'Bulletin février', 'Relevé 2025');
    await file(user, 'Box internet', 'Bulletin de paie', 3);

    await waitFor(() => expect(sent.ids()).toEqual(['a', 'b', 'c']));
    await waitFor(async () => expect(await titles()).toEqual(['Bulletin février']));
    expect(toasts()).toHaveTextContent('2 documents classés.');
    expect(screen.getByText('1 document sélectionné')).toBeInTheDocument();
    expect(within(await table()).getByRole('checkbox', { name: 'Sélectionner Bulletin février' })).toHaveAttribute('aria-checked', 'true');
    const refused = screen.getByRole('list', { name: 'Documents non classés' });
    expect(refused).toHaveTextContent('Bulletin février');
    expect(within(refused).getByRole('alert')).toHaveTextContent("Vous n'avez pas le droit de faire cette action.");
    await waitFor(() => expect(screen.getByRole('button', { name: 'Classer' })).toHaveFocus());
  });

  it('a document whose preview is refused is not linked, the others are', async () => {
    const sent = serveInbox();
    server.use(http.get('*/api/v1/documents/:id/links/preview', ({ params }) =>
      (params.id === 'a' ? problem(409, 'documents.already_linked') : HttpResponse.json({ gainedReaders: [] }))));
    const { user } = await renderApp('/documents?tab=inbox');
    await select(user, 'Bulletin janvier', 'Bulletin février');
    await file(user);

    await waitFor(() => expect(sent.ids()).toEqual(['b']));
    await waitFor(async () => expect(await titles()).toEqual(['Bulletin janvier', 'Relevé 2025']));
    expect(screen.getByRole('list', { name: 'Documents non classés' })).toHaveTextContent('Bulletin janvier');
  });

  it('a document the reader cannot edit cannot be selected', async () => {
    server.use(
      http.get('*/api/v1/documents', () => HttpResponse.json(list([summary({ id: 'a', title: 'Avis', links: [], canEdit: false })]))),
    );
    await renderApp('/documents?tab=inbox');
    expect(within(await table()).getByRole('checkbox', { name: 'Sélectionner Avis' })).toBeDisabled();
    expect(within(await table()).getByRole('checkbox', { name: 'Tout sélectionner' })).toBeDisabled();
  });
});
