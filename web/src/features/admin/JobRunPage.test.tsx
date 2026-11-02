import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const id = '7f0c6b3e-1d1a-4c55-9a43-1f0b9e1c2d3e';
const url = `/admin/jobs/irl-weekly/runs/${id}`;
const run = { id, trigger: 'scheduled', startedAt: '2026-11-02T07:00:00+00:00', finishedAt: '2026-11-02T07:00:02+00:00', durationMs: 2000, outcome: 'failed', summary: 'InvalidOperationException: boom' };
const answer = (body: object, status = 200) => server.use(http.get(`*/api/v1/admin/jobs/irl-weekly/runs/${id}`, () => HttpResponse.json(body, { status })));

describe('Administration › Tâches › une exécution', () => {
  it('shows the error and the logs of a run', async () => {
    answer({ ...run, logs: ['2026-11-02T08:00:01.000+01:00 [ERR] Nid.Host.Jobs.IrlWeeklyJob: irl-weekly crashed'], logsAvailable: true });
    const { container } = await renderApp(url, { capabilities: adminCapabilities });

    expect(await screen.findByText('InvalidOperationException: boom')).toBeInTheDocument();
    expect(screen.getByText(/irl-weekly crashed/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Indice de référence des loyers' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('tells a run without logs from file logs turned off', async () => {
    answer({ ...run, logs: null, logsAvailable: true });
    const first = await renderApp(url, { capabilities: adminCapabilities });
    expect(await screen.findByText('Aucun log pour cette exécution.')).toBeInTheDocument();
    first.unmount();

    answer({ ...run, logs: null, logsAvailable: false });
    await renderApp(url, { capabilities: adminCapabilities });
    expect(await screen.findByText(/logs fichier sont désactivés/)).toBeInTheDocument();
  });

  it('says when the run does not exist', async () => {
    answer({ type: 'about:blank', title: 'Not found', status: 404, code: 'job_run.not_found' }, 404);
    await renderApp(url, { capabilities: adminCapabilities });

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
