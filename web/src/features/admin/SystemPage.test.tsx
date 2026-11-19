import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { formatMegabytes } from './format';

const status = (overrides: object) => ({
  version: '1.0.0+abc123', startedAt: '2026-09-24T06:00:00+00:00',
  health: { database: 'healthy', identityProvider: 'unhealthy' }, databaseSizeBytes: 12_345_678,
  auth: { mode: 'oidc', adminSource: 'identity_provider', insecureHttp: false },
  ...overrides,
});

describe('Administration › Système', () => {
  it('shows the version, the health and the database size', async () => {
    server.use(http.get('*/api/v1/admin/system', () => HttpResponse.json(status({}))));
    const { container } = await renderApp('/admin/system', { capabilities: adminCapabilities });

    expect(await screen.findByText('1.0.0+abc123')).toBeInTheDocument();
    expect(screen.getByText('Base de données').nextElementSibling).toHaveTextContent('disponible');
    expect(screen.getByText("Fournisseur d'identité").nextElementSibling).toHaveTextContent('injoignable');
    expect(screen.getByText('Connexion').nextElementSibling).toHaveTextContent("fournisseur d'identité");
    expect(screen.getByText(`${formatMegabytes(12_345_678)} Mo`)).toBeInTheDocument();
    expect(screen.queryByText(/HTTP sans chiffrement/)).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says local accounts have no identity provider to reach, and warns when plain HTTP is allowed', async () => {
    server.use(http.get('*/api/v1/admin/system', () => HttpResponse.json(status({
      health: { database: 'healthy', identityProvider: 'not_used' },
      auth: { mode: 'local', adminSource: 'nid', insecureHttp: true },
    }))));
    const { container } = await renderApp('/admin/system', { capabilities: adminCapabilities });

    expect(await screen.findByText('1.0.0+abc123')).toBeInTheDocument();
    expect(screen.getByText('Connexion').nextElementSibling).toHaveTextContent('comptes locaux');
    expect(screen.getByText("Fournisseur d'identité").nextElementSibling).toHaveTextContent('non utilisé');
    expect(screen.getByRole('status')).toHaveTextContent(/HTTP sans chiffrement est autorisé/);
    await expectNoAxeViolations(container);
  });

  it('warns on the admin overview too', async () => {
    server.use(http.get('*/api/v1/admin/system', () => HttpResponse.json(status({ auth: { mode: 'local', adminSource: 'nid', insecureHttp: true } }))));
    await renderApp('/admin', { capabilities: adminCapabilities });

    expect(await screen.findByText(/HTTP sans chiffrement est autorisé/)).toBeInTheDocument();
  });
});
