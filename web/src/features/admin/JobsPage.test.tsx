import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { formatDuration } from './format';
import { formatInstant } from '@/lib/format';

const failedRun = { id: '7f0c6b3e-1d1a-4c55-9a43-1f0b9e1c2d3e', trigger: 'scheduled', startedAt: '2026-11-02T04:30:00+00:00', finishedAt: '2026-11-02T04:30:12+00:00', durationMs: 12000, outcome: 'failed', summary: 'InvalidOperationException: boom' };

describe('Administration › Tâches', () => {
  it('names each job, says its schedule and its last outcome', async () => {
    server.use(http.get('*/api/v1/admin/jobs', () => HttpResponse.json([
      { key: 'bank-sync-nightly', cron: '0 30 4 * * ?', nextRunAt: '2026-11-03T04:30:00+00:00', lastRun: failedRun },
      { key: 'fx-backfill', cron: null, nextRunAt: null, lastRun: null },
      { key: 'mystery', cron: '0 0/5 * * * ?', nextRunAt: null, lastRun: null },
    ])));
    const { container } = await renderApp('/admin/jobs', { capabilities: adminCapabilities });

    const bank = await screen.findByRole('row', { name: /Synchronisation bancaire/ });
    expect(bank).toHaveTextContent('Récupère les soldes');
    expect(bank).toHaveTextContent(/Chaque jour à \d\d:30/);
    expect(bank).toHaveTextContent('échec');
    expect(bank).toHaveTextContent(formatDuration(12000));
    expect(bank).toHaveTextContent(formatInstant('2026-11-03T04:30:00+00:00'));
    const backfill = screen.getByRole('row', { name: /Historique des taux de change/ });
    expect(backfill).toHaveTextContent('jamais');
    expect(backfill).toHaveTextContent('À la demande');
    expect(screen.getByRole('row', { name: /mystery/ })).toHaveTextContent('0 0/5 * * * ?');
    await expectNoAxeViolations(container);
  });

  it('says when jobs are disabled on this instance', async () => {
    server.use(http.get('*/api/v1/admin/jobs', () => HttpResponse.json([])));
    await renderApp('/admin/jobs', { capabilities: adminCapabilities });

    expect(await screen.findByRole('heading', { name: 'Aucune tâche planifiée' })).toBeInTheDocument();
  });
});
