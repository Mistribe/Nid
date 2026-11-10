import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const run = { id: '7f0c6b3e-1d1a-4c55-9a43-1f0b9e1c2d3e', trigger: 'on_demand', startedAt: '2026-11-02T04:30:00+00:00', finishedAt: '2026-11-02T04:30:12+00:00', durationMs: 12000, outcome: 'failed', summary: 'InvalidOperationException: boom' };

describe('Administration › Tâches › une tâche', () => {
  it('says what the job does, what it depends on, and lists its runs', async () => {
    server.use(http.get('*/api/v1/admin/jobs/bank-sync-nightly', () => HttpResponse.json({ key: 'bank-sync-nightly', cron: '0 30 4 * * ?', nextRunAt: '2026-11-03T04:30:00+00:00', runs: { items: [run], total: 1, page: 1 } })));
    const { container } = await renderApp('/admin/jobs/bank-sync-nightly', { capabilities: adminCapabilities });

    expect(await screen.findByText(/Administration › Banque/)).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1, name: 'Synchronisation bancaire' })).toBeInTheDocument();
    const row = screen.getByRole('row', { name: /InvalidOperationException/ });
    expect(row).toHaveTextContent('À la demande');
    expect(row).toHaveTextContent('échec');
    await expectNoAxeViolations(container);
  });

  it('says when nothing ran yet', async () => {
    server.use(http.get('*/api/v1/admin/jobs/fx-backfill', () => HttpResponse.json({ key: 'fx-backfill', cron: null, nextRunAt: null, runs: { items: [], total: 0, page: 1 } })));
    await renderApp('/admin/jobs/fx-backfill', { capabilities: adminCapabilities });

    expect(await screen.findByText('Aucune exécution enregistrée')).toBeInTheDocument();
  });

  it('the job’s name is the current crumb', async () => {
    server.use(http.get('*/api/v1/admin/jobs/bank-sync-nightly', () => HttpResponse.json({ key: 'bank-sync-nightly', cron: null, nextRunAt: null, runs: { items: [], total: 0, page: 1 } })));
    await renderApp('/admin/jobs/bank-sync-nightly', { capabilities: adminCapabilities });

    await screen.findByText('Aucune exécution enregistrée');
    const trail = within(screen.getByRole('navigation', { name: 'Fil d’Ariane' }));
    expect(trail.getByRole('link', { name: 'Tâches planifiées' })).toBeInTheDocument();
    expect(trail.getByText('Synchronisation bancaire')).toHaveAttribute('aria-current', 'page');
  });

  it('pages the history', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/admin/jobs/bank-sync-nightly', ({ request }) => {
      seen.push(request.url);
      return HttpResponse.json({ key: 'bank-sync-nightly', cron: null, nextRunAt: null, runs: { items: [run], total: 51, page: 1 } });
    }));
    const { user, router } = await renderApp('/admin/jobs/bank-sync-nightly', { capabilities: adminCapabilities });

    const pages = await screen.findByRole('navigation', { name: 'Pages' });
    expect(pages).toHaveTextContent('Page 1 sur 2');
    await user.click(within(pages).getByRole('button', { name: 'Suivant' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ page: 2 }));
    await waitFor(() => expect(seen.at(-1)).toContain('page=2'));
  });
});
