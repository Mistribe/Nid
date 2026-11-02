import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { formatMegabytes } from './format';

describe('Administration › Système', () => {
  it('shows the version, the health and the database size', async () => {
    server.use(http.get('*/api/v1/admin/system', () => HttpResponse.json({
      version: '1.0.0+abc123', startedAt: '2026-09-24T06:00:00+00:00',
      health: { database: 'healthy', identityProvider: 'unhealthy' }, databaseSizeBytes: 12_345_678,
    })));
    const { container } = await renderApp('/admin/system', { capabilities: adminCapabilities });

    expect(await screen.findByText('1.0.0+abc123')).toBeInTheDocument();
    expect(screen.getByText('Base de données').nextElementSibling).toHaveTextContent('disponible');
    expect(screen.getByText("Fournisseur d'identité").nextElementSibling).toHaveTextContent('injoignable');
    expect(screen.getByText(`${formatMegabytes(12_345_678)} Mo`)).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });
});
