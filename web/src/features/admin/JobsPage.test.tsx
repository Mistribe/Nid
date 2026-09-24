import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { formatInstant } from './format';

describe('Administration › Tâches', () => {
  it('lists the jobs with their last and next run', async () => {
    server.use(http.get('*/api/v1/admin/jobs', () => HttpResponse.json([
      { key: 'fx-backfill', lastRunAt: null, nextRunAt: null, state: 'idle' },
      { key: 'fx-daily', lastRunAt: '2026-09-24T07:00:00+00:00', nextRunAt: '2026-09-25T07:00:00+00:00', state: 'running' },
    ])));
    const { container } = await renderApp('/admin/jobs', { capabilities: adminCapabilities });

    const daily = await screen.findByRole('row', { name: /fx-daily/ });
    expect(daily).toHaveTextContent(formatInstant('2026-09-25T07:00:00+00:00'));
    expect(daily).toHaveTextContent('en cours');
    expect(screen.getByRole('row', { name: /fx-backfill/ })).toHaveTextContent('jamais');
    await expectNoAxeViolations(container);
  });

  it('says when jobs are disabled on this instance', async () => {
    server.use(http.get('*/api/v1/admin/jobs', () => HttpResponse.json([])));
    await renderApp('/admin/jobs', { capabilities: adminCapabilities });

    expect(await screen.findByRole('heading', { name: 'Aucune tâche planifiée' })).toBeInTheDocument();
  });
});
