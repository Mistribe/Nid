import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { serveDocument } from './serveDocument';
import { ALICE, DOC, target } from './testData';

const BOX = '0192f0aa-0000-7000-8000-00000000e002';

async function choose(user: Awaited<ReturnType<typeof renderApp>>['user'], target = 'Box internet', role = 'Facture') {
  await user.click(await screen.findByRole('button', { name: 'Ajouter un lien' }));
  await screen.findByRole('dialog', { name: 'Ajouter un lien' });
  await pick(user, 'Élément', target);
  await pick(user, 'Rôle', role);
  await user.click(screen.getByRole('button', { name: 'Ajouter' }));
}

describe('AddLinkDialog', () => {
  beforeAll(async () => { await import('./DocumentDetailPage'); await import('./AddLinkDialog'); }, 30_000);

  it('lists the targets, the already linked one aside, and those Alice cannot write are not selectable', async () => {
    serveDocument();
    const { user } = await renderApp(`/documents/${DOC}`);
    await user.click(await screen.findByRole('button', { name: 'Ajouter un lien' }));
    await screen.findByRole('dialog', { name: 'Ajouter un lien' });
    await user.click(screen.getByRole('combobox', { name: 'Élément' }));
    const list = await screen.findByRole('listbox');

    expect(within(list).getAllByRole('option').map((o) => o.textContent)).toEqual(['Voiture de Bob (lecture seule)', 'Box internet', 'Alice', 'Foyer']);
    const readOnly = within(list).getByRole('option', { name: 'Voiture de Bob (lecture seule)' });
    expect(readOnly).toHaveAttribute('aria-disabled', 'true');
    await user.click(readOnly);
    expect(screen.getByRole('combobox', { name: 'Élément' })).toHaveTextContent('Choisir un élément');
  });

  it('leaves out a person the actor cannot link to', async () => {
    serveDocument({ targets: { items: [target(), target({ kind: 'core.person', id: '0192f0aa-0000-7000-8000-00000000a002', label: 'Bob', canWrite: false }),
      target({ kind: 'core.person', id: ALICE, label: 'Alice' })] } });
    const { user } = await renderApp(`/documents/${DOC}`);
    await user.click(await screen.findByRole('button', { name: 'Ajouter un lien' }));
    await user.click(await screen.findByRole('combobox', { name: 'Élément' }));
    expect(within(await screen.findByRole('listbox')).getAllByRole('option').map((o) => o.textContent)).toEqual(['Alice']);
  });

  it('adds at once when nobody gains the document', async () => {
    const { sent } = serveDocument({ preview: [] });
    const { user } = await renderApp(`/documents/${DOC}`);
    await choose(user);

    await waitFor(() => expect(sent.map((s) => s.method)).toEqual(['GET', 'POST']));
    expect(sent[0]).toMatchObject({ path: `/api/v1/documents/${DOC}/links/preview`, search: `?targetKind=core.item&targetId=${BOX}` });
    expect(sent[1]).toMatchObject({ path: `/api/v1/documents/${DOC}/links`, body: { targetKind: 'core.item', targetId: BOX, role: 'invoice' } });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.getByText('Lien ajouté.')).toBeInTheDocument();
  });

  it('asks first when the link lets Bob read the document, and adds once confirmed', async () => {
    const { writes } = serveDocument({ preview: [{ id: 'b', name: 'Bob' }] });
    const { user } = await renderApp(`/documents/${DOC}`);
    await choose(user);

    expect(await screen.findByText('Bob pourra lire ce document.')).toBeInTheDocument();
    expect(writes()).toEqual([]);
    await user.click(screen.getByRole('button', { name: 'Ajouter quand même' }));
    await waitFor(() => expect(writes()).toEqual([expect.objectContaining({ method: 'POST', body: { targetKind: 'core.item', targetId: BOX, role: 'invoice' } })]));
  });

  it('names every reader gained', async () => {
    serveDocument({ preview: [{ id: 'b', name: 'Bob' }, { id: 'c', name: 'Chloé' }] });
    const { user } = await renderApp(`/documents/${DOC}`);
    await choose(user, 'Foyer', 'Avis d’imposition');
    expect(await screen.findByText('Bob et Chloé pourront lire ce document.')).toBeInTheDocument();
  });

  it('posts nothing when the widening is cancelled', async () => {
    const { writes } = serveDocument({ preview: [{ id: 'b', name: 'Bob' }] });
    const { user } = await renderApp(`/documents/${DOC}`);
    await choose(user);
    await screen.findByText('Bob pourra lire ce document.');
    await user.click(screen.getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(writes()).toEqual([]);
  });

  it('shows a refused preview (already linked) and posts nothing, the dialog stays open', async () => {
    const { writes } = serveDocument();
    server.use(http.get('*/api/v1/documents/:id/links/preview', () => HttpResponse.json(
      { status: 409, code: 'documents.already_linked', title: 'Already linked.' }, { status: 409, headers: { 'content-type': 'application/problem+json' } })));
    const { user } = await renderApp(`/documents/${DOC}`);
    await choose(user);

    expect(await screen.findByText('Cet élément a changé entre-temps. Rechargez la page pour voir sa dernière version.')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Ajouter un lien' })).toBeInTheDocument();
    expect(writes()).toEqual([]);
  });
});
