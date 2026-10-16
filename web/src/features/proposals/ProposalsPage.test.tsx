import { screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { proposal } from './testData';

const serve = (byStatus: Record<string, unknown[]>) =>
  server.use(http.get('*/api/v1/core/proposals', ({ request }) =>
    HttpResponse.json({ items: byStatus[new URL(request.url).searchParams.get('status') ?? 'pending'] ?? [] })));

describe('À valider', () => {
  beforeAll(async () => { await import('./ProposalsPage'); });

  it('groups the pending proposals by statement and summarises each', async () => {
    serve({ pending: [
      proposal({ sourceLabel: 'Relevé PEA septembre' }),
      proposal({ targetName: 'Assurance-vie', payload: { asOf: '2025-09-30', amount: { amount: '1200.50', currency: 'EUR' } }, sourceLabel: null, createdVia: 'api' }),
    ] });
    const { container } = await renderApp('/proposals');

    const statement = await screen.findByRole('region', { name: 'Relevé PEA septembre' });
    expect(within(statement).getByRole('link', { name: /PEA — valeur au 30 sept\. 2025 : 23\s500,00\s€/ })).toBeInTheDocument();
    expect(statement).toHaveTextContent('par l’assistant (MCP)');
    const none = screen.getByRole('region', { name: 'Sans relevé' });
    expect(none).toHaveTextContent('Assurance-vie');
    expect(none).toHaveTextContent('par l’API');
    await expectNoAxeViolations(container);
  });

  it('puts the group with the newest proposal first, « Sans relevé » included', async () => {
    serve({ pending: [
      proposal({ sourceLabel: 'Relevé ancien', createdAt: '2026-10-01T08:00:00+00:00' }),
      proposal({ sourceLabel: null, createdAt: '2026-10-15T08:00:00+00:00' }),
      proposal({ sourceLabel: 'Relevé récent', createdAt: '2026-10-10T08:00:00+00:00' }),
    ] });
    await renderApp('/proposals');

    await screen.findByRole('region', { name: 'Relevé ancien' });
    const names = screen.getAllByRole('region').map((r) => r.getAttribute('aria-label'));
    expect(names.filter((n) => ['Sans relevé', 'Relevé récent', 'Relevé ancien'].includes(n ?? ''))).toEqual(['Sans relevé', 'Relevé récent', 'Relevé ancien']);
  });

  it('says when nothing waits, and shows the decided ones on their tab', async () => {
    serve({ approved: [proposal({ status: 'approved', decidedAt: '2026-10-16T09:00:00+00:00' })] });
    const { user } = await renderApp('/proposals');

    expect(await screen.findByText('Rien à valider pour l’instant.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'En attente' })).toHaveAttribute('aria-current', 'page');
    await user.click(screen.getByRole('link', { name: 'Approuvées' }));
    expect(await screen.findByText(/Approuvée le/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Approuvées' })).toHaveAttribute('aria-current', 'page');
    await user.click(screen.getByRole('link', { name: 'Rejetées' }));
    expect(await screen.findByText('Aucune proposition rejetée.')).toBeInTheDocument();
  });

  // Review Focus 1.
  it('shows a hostile label and note as plain text', async () => {
    serve({ pending: [proposal({ sourceLabel: '<img src=x onerror=alert(1)> **gras**' })] });
    const { container } = await renderApp('/proposals');

    expect(await screen.findByRole('region', { name: '<img src=x onerror=alert(1)> **gras**' })).toBeInTheDocument();
    expect(container.querySelector('img')).toBeNull();
  });

  // Review Focus 5.
  it('an unknown kind is listed under its raw name', async () => {
    serve({ pending: [proposal({ kind: 'realestate.future_kind', payload: { x: 1 } })] });
    await renderApp('/proposals');

    expect(await screen.findByRole('link', { name: /realestate\.future_kind/ })).toBeInTheDocument();
  });

  it('counts the pending proposals in the navigation, and shows nothing at zero', async () => {
    serve({ pending: [proposal(), proposal()] });
    await renderApp('/dashboard');

    const nav = await screen.findByRole('navigation', { name: 'Navigation principale' });
    expect(await within(nav).findByText('2')).toBeInTheDocument();
    expect(within(nav).getByRole('link', { name: /À valider/ })).toHaveAccessibleName(/2 en attente/);
  });

  it('counts them on the phone’s « Plus » tab and on /more', async () => {
    serve({ pending: [proposal(), proposal()] });
    await renderApp('/more');

    const tabs = screen.getByRole('navigation', { name: 'Onglets' });
    await waitFor(() => expect(within(tabs).getByRole('link', { name: /Plus/ })).toHaveAccessibleName(/2 en attente/));
    const list = await screen.findByRole('list', { name: 'Autres sections' });
    expect(within(list).getByRole('link', { name: /À valider/ })).toHaveAccessibleName(/2 en attente/);
  });

  it('shows the list loading while a status is read', async () => {
    serve({ pending: [] });
    server.use(http.get('*/api/v1/core/proposals', async () => { await delay('infinite'); return HttpResponse.json({ items: [] }); }));
    await renderApp('/proposals');

    expect(await screen.findByRole('status', { name: 'Chargement des propositions' })).toHaveAttribute('aria-busy', 'true');
  });

  it('shows no count when nothing waits', async () => {
    serve({ pending: [] });
    await renderApp('/proposals');

    expect(await screen.findByText('Rien à valider pour l’instant.')).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: 'Navigation principale' });
    expect(within(nav).getByRole('link', { name: 'À valider' })).toBeInTheDocument();
    expect(within(nav).queryByText('0')).toBeNull();
  });

  it('summarises a statement by its date and its movement count', async () => {
    serve({ pending: [proposal({ kind: 'investments.record_statement', payload: {
      asOf: '2025-09-30', movements: [
        { on: '2025-09-05', kind: 'contribution', amount: '150.00' }, { on: '2025-09-12', kind: 'withdrawal', amount: '60.00' },
      ] } })] });
    await renderApp('/proposals');

    expect(await screen.findByRole('link', { name: /PEA — relevé au 30 sept\. 2025 \(2 mouvements\)/ })).toBeInTheDocument();
  });
});
