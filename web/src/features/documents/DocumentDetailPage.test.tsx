import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { stubLocalDay } from '@/test/timeZone';
import { serveDocument } from './serveDocument';
import { DOC, detail, target } from './testData';

const PATH = `/documents/${DOC}`;

describe('DocumentDetailPage', () => {
  beforeAll(async () => { await import('./DocumentDetailPage'); }, 30_000);

  it('goes back to the list when a refetch answers 404 (the actor lost the last link that let them read it)', async () => {
    const api = serveDocument();
    const { user, router } = await renderApp(PATH);
    await screen.findByRole('heading', { level: 1, name: 'Bail 2024' });
    server.use(http.get('*/api/v1/documents/:id', () => HttpResponse.json({ status: 404, title: 'Not found.' }, { status: 404 })));
    await user.click(screen.getByRole('button', { name: 'Retirer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/documents'));
    expect(api.writes()).toHaveLength(1);
  });

  it('shows the local day of the upload', async () => {
    stubLocalDay(13); // Auckland
    serveDocument({ doc: detail({ uploadedAt: '2026-10-26T23:30:00Z' }) });
    await renderApp(PATH);
    const figures = await screen.findByRole('region', { name: 'Chiffres du document' });
    expect(within(figures).getByText('Déposé le').nextSibling).toHaveTextContent('27 octobre 2026');
  });

  it('shows the title, then the file type, size, upload day and uploader', async () => {
    serveDocument();
    const { container } = await renderApp(PATH);

    expect(await screen.findByRole('heading', { level: 1, name: 'Bail 2024' })).toBeInTheDocument();
    const figures = screen.getByRole('region', { name: 'Chiffres du document' });
    expect(within(figures).getByText('Type de fichier').nextSibling).toHaveTextContent('PDF');
    expect(within(figures).getByText('Taille').nextSibling).toHaveTextContent('245 Ko');
    expect(within(figures).getByText('Déposé le').nextSibling).toHaveTextContent('20 octobre 2026');
    expect(await within(figures).findByText('Alice')).toBeInTheDocument();
    expect(screen.getByText('Signé chez le notaire')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows no uploader when the persons do not include them', async () => {
    serveDocument({ targets: { items: [target()] } });
    await renderApp(PATH);
    await screen.findByRole('heading', { level: 1, name: 'Bail 2024' });
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Rôle pour Appartement Lyon' })).toBeInTheDocument());
    expect(screen.queryByText('Par')).not.toBeInTheDocument();
  });

  it('« Télécharger » links to the content', async () => {
    serveDocument();
    await renderApp(PATH);
    expect(await screen.findByRole('link', { name: 'Télécharger' })).toHaveAttribute('href', `/api/v1/documents/${DOC}/content`);
  });

  it('archives after a confirmation, then offers « Désarchiver »', async () => {
    const { writes } = serveDocument();
    const { user } = await renderApp(PATH);
    await user.click(await screen.findByRole('button', { name: 'Archiver' }));
    expect(writes()).toEqual([]);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(writes().map((w) => w.path)).toEqual([`/api/v1/documents/${DOC}/archive`]));
    await user.click(await screen.findByRole('button', { name: 'Désarchiver' }));
    await waitFor(() => expect(writes().map((w) => w.path).at(-1)).toBe(`/api/v1/documents/${DOC}/unarchive`));
  });

  it('keeps the focus on « Modifier » after archiving and after unarchiving', async () => {
    serveDocument();
    const { user } = await renderApp(PATH);
    await user.click(await screen.findByRole('button', { name: 'Archiver' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await user.click(await screen.findByRole('button', { name: 'Désarchiver' }));
    await screen.findByRole('button', { name: 'Archiver' });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Modifier' })).toHaveFocus());
  });

  it('keeps the page when a background refetch fails', async () => {
    serveDocument();
    const { queryClient } = await renderApp(PATH);
    await screen.findByRole('heading', { level: 1, name: 'Bail 2024' });
    server.use(http.get(`*/api/v1/documents/${DOC}`, () => HttpResponse.json({ status: 500, title: 'Boom' }, { status: 500 })));
    await queryClient.refetchQueries({ queryKey: ['get', '/api/v1/documents/{id}'] });
    await waitFor(() => expect(queryClient.getQueryCache().findAll({ queryKey: ['get', '/api/v1/documents/{id}'] }).some((q) => q.state.status === 'error')).toBe(true));

    expect(screen.getByRole('heading', { level: 1, name: 'Bail 2024' })).toBeInTheDocument();
  });

  it('a reader who cannot edit sees only « Télécharger »', async () => {
    serveDocument({ doc: detail({ canEdit: false }) });
    await renderApp(PATH);
    await screen.findByRole('link', { name: 'Télécharger' });
    expect(screen.queryByRole('button', { name: 'Modifier' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Archiver' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ajouter un lien' })).not.toBeInTheDocument();
  });

  it('a missing document shows the not-found state', async () => {
    serveDocument();
    server.use(http.get(`*/api/v1/documents/${DOC}`, () => HttpResponse.json({ status: 404, title: 'Not found.' }, { status: 404 })));
    await renderApp(PATH);
    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Retour aux documents' })).toHaveAttribute('href', '/documents');
  });
});
