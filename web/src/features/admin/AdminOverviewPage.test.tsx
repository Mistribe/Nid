import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities, allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const run = (outcome: string) => ({ id: '7f0c6b3e-1d1a-4c55-9a43-1f0b9e1c2d3e', trigger: 'scheduled', startedAt: '2026-11-02T04:30:00+00:00', finishedAt: '2026-11-02T04:30:12+00:00', durationMs: 12000, outcome, summary: null });
const job = (key: string, outcome: string | null) => ({ key, cron: '0 30 4 * * ?', nextRunAt: null, lastRun: outcome ? run(outcome) : null });
const main = () => within(screen.getByRole('main'));
const item = (name: string) => main().getByRole('link', { name }).closest('li')!;

const SECTIONS: [string, string][] = [
  ['Taux de change', '/admin/fx'], ['Cours', '/admin/prices'], ['IRL', '/admin/irl'], ['Inflation', '/admin/inflation'],
  ['Tâches', '/admin/jobs'], ['Banque', '/admin/banking'], ['Catalogue', '/admin/catalog'],
  ['Utilisateurs', '/admin/users'], ['Foyer', '/admin/household'], ['Journal de sécurité', '/admin/security'],
  ['Système', '/admin/system'], ['Notifications', '/admin/notifications'],
];

describe('Administration › vue d’ensemble', () => {
  it('lists every section by group', async () => {
    await renderApp('/admin', { capabilities: adminCapabilities });

    await screen.findByRole('heading', { level: 1, name: 'Administration' });
    expect(main().getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual(['Données de référence', 'Opérations', 'Accès', 'Système']);
    const links = main().getAllByRole('link').filter((a) => a.getAttribute('href')?.startsWith('/admin/'));
    expect(links.map((a) => a.getAttribute('href'))).toEqual(expect.arrayContaining(SECTIONS.map(([, to]) => to)));
    for (const [, to] of SECTIONS) expect(links.filter((a) => a.getAttribute('href') === to)).toHaveLength(1);
  });

  it('shows a section’s last run from its job', async () => {
    server.use(http.get('*/api/v1/admin/jobs', () => HttpResponse.json([job('fx-daily', 'failed')])));
    await renderApp('/admin', { capabilities: adminCapabilities });

    await screen.findByText('échec');
    expect(item('Taux de change')).toHaveTextContent('échec');
  });

  it('counts the jobs whose last run failed', async () => {
    server.use(http.get('*/api/v1/admin/jobs', () => HttpResponse.json([job('fx-daily', 'failed'), job('catalog-sync', 'failed'), job('prices-daily', 'succeeded')])));
    await renderApp('/admin', { capabilities: adminCapabilities });

    expect(await screen.findByText('2 tâches en échec')).toBeInTheDocument();
  });

  it('shows no state when jobs and system fail', async () => {
    server.use(
      http.get('*/api/v1/admin/jobs', () => new HttpResponse(null, { status: 500 })),
      http.get('*/api/v1/admin/system', () => new HttpResponse(null, { status: 500 })),
    );
    await renderApp('/admin', { capabilities: adminCapabilities });

    expect(await within(await screen.findByRole('main')).findByRole('link', { name: 'Taux de change' })).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 100));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText(/échec|disponible|injoignable/)).not.toBeInTheDocument();
  });

  it('says the system is healthy', async () => {
    server.use(http.get('*/api/v1/admin/system', () => HttpResponse.json({
      version: '1.0.0', startedAt: '2026-09-24T06:00:00+00:00', health: { database: 'healthy', identityProvider: 'healthy' }, databaseSizeBytes: 1000,
    })));
    await renderApp('/admin', { capabilities: adminCapabilities });

    await screen.findByText('disponible');
    expect(item('Système')).toHaveTextContent('disponible');
  });

  it('sends a non-admin to the forbidden page', async () => {
    await renderApp('/admin', { capabilities: allCapabilities });

    expect(await screen.findByRole('heading', { level: 1, name: 'Accès réservé' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = await renderApp('/admin', { capabilities: adminCapabilities });

    await screen.findByRole('heading', { level: 1, name: 'Administration' });
    await expectNoAxeViolations(container);
  });
});
