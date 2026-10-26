import { screen, waitFor } from '@testing-library/react';
import { HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { todayIso } from '../subscriptions/today';
import { serveDocument } from './serveDocument';
import { DOC } from './testData';

const open = async (user: Awaited<ReturnType<typeof renderApp>>['user']) => {
  await user.click(await screen.findByRole('button', { name: 'Modifier' }));
  return screen.findByRole('dialog', { name: 'Modifier le document' });
};

describe('EditDocumentDialog', () => {
  beforeAll(async () => { await import('./DocumentDetailPage'); await import('./EditDocumentDialog'); }, 30_000);

  it('edits the title, date and note, and sends them', async () => {
    const { writes } = serveDocument();
    const { user } = await renderApp(`/documents/${DOC}`);
    await open(user);

    const title = screen.getByLabelText('Titre');
    expect(title).toHaveValue('Bail 2024');
    expect(screen.getByLabelText('Date du document')).toHaveValue('01/03/2024');
    await user.clear(title);
    await user.type(title, 'Bail Lyon');
    const date = screen.getByLabelText('Date du document');
    await user.clear(date);
    await user.type(date, '02/03/2024');
    const note = screen.getByRole('textbox', { name: 'Note' });
    await user.clear(note);
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(writes()[0]).toMatchObject({ method: 'PUT', path: `/api/v1/documents/${DOC}`, body: { title: 'Bail Lyon', documentDate: '2024-03-02', note: null } });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.getByText('Document enregistré.')).toBeInTheDocument();
  });

  it('refuses a future date before sending', async () => {
    const { writes } = serveDocument();
    const { user } = await renderApp(`/documents/${DOC}`);
    await open(user);
    const date = screen.getByLabelText('Date du document');
    await user.clear(date);
    await user.type(date, `01/01/${Number(todayIso().slice(0, 4)) + 1}`);
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByText('La date ne peut pas être dans le futur.')).toBeInTheDocument();
    expect(writes()).toEqual([]);
  });

  it('shows the server’s field error on its field', async () => {
    const { writes } = serveDocument({
      put: () => HttpResponse.json({ status: 400, code: 'documents.invalid_field', title: 'A note is at most 2000 characters.',
        errors: [{ path: 'note', code: 'documents.invalid_field', message: 'A note is at most 2000 characters.' }] }, { status: 400 }),
    });
    const { user } = await renderApp(`/documents/${DOC}`);
    await open(user);
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(await screen.findByText('La note compte au plus 2 000 caractères.')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Note' })).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });
});
