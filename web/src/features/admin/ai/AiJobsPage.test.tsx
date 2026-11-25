import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const counts = { queued: 2, running: 1, succeeded: 40, failed: 0, dead: 1 };

const job = (over: Record<string, unknown> = {}) => ({
  id: 'j1', type: 'document-text', status: 'queued', attempts: 0, waitingReason: 'ai.no_worker', error: null, workerName: null,
  createdAt: '2026-11-22T08:00:00+00:00', finishedAt: null, file: null, dispatches: [], ...over,
});

describe('Réglages › IA › Tâches', () => {
  it('says in words why a job waits or failed', async () => {
    server.use(http.get('*/api/v1/ai/jobs', () => HttpResponse.json({
      items: [
        job(),
        job({ id: 'j2', type: 'echo', status: 'dead', attempts: 3, waitingReason: null, error: 'ai.timed_out', workerName: 'Box', finishedAt: '2026-11-22T09:00:00+00:00' }),
        // A worker's own failure comes after the code (JobDispatcher): the words are those of the code.
        job({ id: 'j3', type: 'document-text', status: 'queued', attempts: 1, waitingReason: 'ai.worker_failed:oom' }),
      ],
      total: 3, page: 1, types: ['document-text', 'echo'], counts,
    })));
    const { container } = await renderApp('/admin/ai/jobs', { capabilities: adminCapabilities });

    expect(await screen.findByRole('row', { name: /Aucun worker ne sait faire ce travail/ })).toHaveTextContent('document-text');
    const dead = screen.getByRole('row', { name: /Test d'un worker/ });
    expect(dead).toHaveTextContent('Abandonnée');
    expect(dead).toHaveTextContent('Le worker a mis trop de temps.');
    expect(dead).toHaveTextContent('Box');
    expect(screen.getByRole('row', { name: /réessaiera/ })).toHaveTextContent('Le worker a échoué ; Nid réessaiera.');
    expect(screen.queryByText(/ai\./)).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows how many jobs stand in each state, as the server counts them', async () => {
    server.use(http.get('*/api/v1/ai/jobs', () => HttpResponse.json({ items: [job()], total: 1, page: 1, types: ['document-text'], counts })));
    await renderApp('/admin/ai/jobs', { capabilities: adminCapabilities });

    const line = (await screen.findByText('Terminées')).closest('dl')!;
    expect(within(line).getByText('Terminées').nextSibling).toHaveTextContent('40');
    expect(within(line).getByText('En cours').nextSibling).toHaveTextContent('1');
    expect(within(line).getByText('Abandonnées').nextSibling).toHaveTextContent('1');
  });

  it('names the document a job is about and lists each time it was sent (#602)', async () => {
    server.use(http.get('*/api/v1/ai/jobs', () => HttpResponse.json({
      items: [
        job({
          status: 'succeeded', attempts: 1, waitingReason: null, workerName: 'Box', finishedAt: '2026-11-22T08:05:00+00:00',
          file: { module: 'documents', reference: 'd1' },
          dispatches: [
            { workerName: 'Atelier', sentAt: '2026-11-22T08:00:00+00:00', finishedAt: '2026-11-22T08:01:00+00:00', outcome: 'ai.worker_failed' },
            { workerName: 'Box', sentAt: '2026-11-22T08:02:00+00:00', finishedAt: '2026-11-22T08:05:00+00:00', outcome: 'succeeded' },
          ],
        }),
        // A document the admin does not read: the server gives no reference.
        job({ id: 'j2', file: { module: 'documents', reference: null } }),
      ],
      total: 2, page: 1, types: ['document-text'], counts,
    })), http.get('*/api/v1/documents/d1', () => HttpResponse.json({ document: { id: 'd1', title: 'Quittance de loyer' } })));
    const { user } = await renderApp('/admin/ai/jobs', { capabilities: adminCapabilities });

    // The table (the mobile list repeats it).
    const table = await screen.findByRole('table');
    expect(await within(table).findByRole('link', { name: 'Quittance de loyer' })).toHaveAttribute('href', '/documents/d1');
    expect(within(table).getAllByRole('link')).toHaveLength(1);
    expect(within(table).getByRole('row', { name: /Aucun worker ne sait faire ce travail/ })).toHaveTextContent('Document');

    await user.click(within(table).getByRole('button', { name: '2 envois' }));
    const dialog = await screen.findByRole('dialog', { name: /Envois de la tâche/ });
    const items = within(dialog).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('Atelier');
    expect(items[0]).toHaveTextContent('Le worker a échoué ; Nid réessaiera.');
    expect(items[1]).toHaveTextContent('Box');
    expect(items[1]).toHaveTextContent('Réussi');
  });

  it('sends the filters and the page to the server, and a new filter starts at the first page', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/ai/jobs', ({ request }) => {
      seen.push(new URL(request.url).search);
      return HttpResponse.json({ items: [job()], total: 51, page: 1, types: ['document-text', 'echo'], counts });
    }));
    const { user, router } = await renderApp('/admin/ai/jobs?status=queued', { capabilities: adminCapabilities });

    const pages = await screen.findByRole('navigation', { name: 'Pages' });
    await user.click(within(pages).getByRole('button', { name: 'Suivant' }));
    await waitFor(() => expect(seen.at(-1)).toBe('?status=queued&page=2'));

    await pick(user, 'Type', "Test d'un worker");
    await waitFor(() => expect(router.state.location.search).toEqual({ status: 'queued', type: 'echo' }));
    await waitFor(() => expect(seen.at(-1)).toBe('?status=queued&type=echo&page=1'));
  });

  it('tells an empty list from one its filters empty', async () => {
    server.use(http.get('*/api/v1/ai/jobs', () => HttpResponse.json({ items: [], total: 0, page: 1, types: [], counts })));
    const { unmount } = await renderApp('/admin/ai/jobs', { capabilities: adminCapabilities });
    expect(await screen.findByRole('heading', { name: 'Aucune tâche' })).toBeInTheDocument();
    unmount();

    await renderApp('/admin/ai/jobs?type=echo', { capabilities: adminCapabilities });
    expect(await screen.findByText('Aucune tâche ne correspond à ces filtres.')).toBeInTheDocument();
  });

  it('drops an unknown status from the address', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/ai/jobs', ({ request }) => {
      seen.push(new URL(request.url).search);
      return HttpResponse.json({ items: [], total: 0, page: 1, types: [], counts });
    }));
    await renderApp('/admin/ai/jobs?status=done', { capabilities: adminCapabilities });

    await waitFor(() => expect(seen.at(-1)).toBe('?page=1'));
  });

  it('takes an old link under Réglages › IA to Administration › IA with its filters (#585)', async () => {
    server.use(http.get('*/api/v1/ai/jobs', () => HttpResponse.json({ items: [], total: 0, page: 1, types: ['echo'], counts })));
    const { router } = await renderApp('/settings/ai/jobs?type=echo', { capabilities: adminCapabilities });

    await waitFor(() => expect(router.state.location.pathname).toBe('/admin/ai/jobs'));
    expect(router.state.location.search).toEqual({ type: 'echo' });
  });
});
