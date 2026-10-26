import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { serveDocument } from './serveDocument';
import { DOC, ITEM, detail, link } from './testData';

const two = () => detail({ links: [link(), link({ targetKind: 'core.household', targetId: '0192f0aa-0000-7000-8000-0000000000f0', role: 'tax_notice', label: 'Foyer' })] });

describe('DocumentLinks', () => {
  beforeAll(async () => { await import('./DocumentDetailPage'); }, 30_000);

  it('lists one row per link with its label and role', async () => {
    serveDocument({ doc: two() });
    await renderApp(`/documents/${DOC}`);
    const list = await screen.findByRole('list', { name: 'Liens du document' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('Appartement Lyon');
    expect(within(rows[0]!).getByRole('combobox', { name: 'Rôle pour Appartement Lyon' })).toHaveTextContent('Bail');
    expect(rows[1]).toHaveTextContent('Foyer');
    expect(within(rows[1]!).getByRole('combobox', { name: 'Rôle pour Foyer' })).toHaveTextContent('Avis d’imposition');
  });

  it('changes a role', async () => {
    const { writes } = serveDocument({ doc: two() });
    const { user } = await renderApp(`/documents/${DOC}`);
    await screen.findByRole('combobox', { name: 'Rôle pour Appartement Lyon' });
    await pick(user, 'Rôle pour Appartement Lyon', 'Assurance');
    await waitFor(() => expect(writes()).toEqual([
      expect.objectContaining({ method: 'PUT', path: `/api/v1/documents/${DOC}/links/core.item/${ITEM}`, body: { role: 'insurance' } }),
    ]));
  });

  it('removes a link after a confirmation', async () => {
    const { writes } = serveDocument({ doc: two() });
    const { user } = await renderApp(`/documents/${DOC}`);
    const list = await screen.findByRole('list', { name: 'Liens du document' });
    await user.click(within(within(list).getAllByRole('listitem')[0]!).getByRole('button', { name: 'Retirer' }));
    expect(writes()).toEqual([]);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(writes()).toEqual([expect.objectContaining({ method: 'DELETE', path: `/api/v1/documents/${DOC}/links/core.item/${ITEM}` })]));
    await waitFor(() => expect(within(screen.getByRole('list', { name: 'Liens du document' })).getAllByRole('listitem')).toHaveLength(1));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ajouter un lien' })).toHaveFocus());
  });

  it('a reader sees the roles as text', async () => {
    serveDocument({ doc: detail({ canEdit: false, links: [link({ canWrite: false })] }) });
    await renderApp(`/documents/${DOC}`);
    const list = await screen.findByRole('list', { name: 'Liens du document' });
    expect(list).toHaveTextContent('Bail');
    expect(within(list).queryByRole('combobox')).not.toBeInTheDocument();
    expect(within(list).queryByRole('button')).not.toBeInTheDocument();
  });

  it('shows the role as text on a link whose target the actor cannot write, even on an editable document', async () => {
    serveDocument({ doc: detail({ links: [link(), link({ targetKind: 'core.household', targetId: '0192f0aa-0000-7000-8000-0000000000f0', role: 'tax_notice', label: 'Foyer', canWrite: false })] }) });
    await renderApp(`/documents/${DOC}`);
    const rows = within(await screen.findByRole('list', { name: 'Liens du document' })).getAllByRole('listitem');
    expect(within(rows[0]!).getByRole('combobox')).toBeInTheDocument();
    expect(rows[1]).toHaveTextContent('Avis d’imposition');
    expect(within(rows[1]!).queryByRole('combobox')).not.toBeInTheDocument();
    expect(within(rows[1]!).queryByRole('button')).not.toBeInTheDocument();
  });

  it('says when the document is linked to nothing', async () => {
    serveDocument({ doc: detail({ links: [] }) });
    await renderApp(`/documents/${DOC}`);
    expect(await screen.findByText('Ce document n’est relié à rien : il attend dans « À classer ».')).toBeInTheDocument();
  });
});
