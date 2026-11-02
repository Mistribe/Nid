import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { delay } from 'msw/utils/delay';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { proposal } from './testData';

const serve = (byStatus: Record<string, unknown[]>) =>
  server.use(http.get('*/api/v1/core/proposals', ({ request }) =>
    HttpResponse.json({ items: byStatus[new URL(request.url).searchParams.get('status') ?? 'pending'] ?? [] })));

/** The body rows of the table (the phone list below `md:` repeats them, so the table is the one queried). */
async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des propositions' });
  return within(table).getAllByRole('row').slice(1);
}

const inTable = async () => within(await screen.findByRole('table', { name: 'Liste des propositions' }));

describe('À valider', () => {
  beforeAll(async () => { await import('./ProposalsPage'); });

  it('shows one table row per proposal with its statement, summary, date and state', async () => {
    serve({ pending: [
      proposal({ sourceLabel: 'Relevé PEA septembre' }),
      proposal({ targetName: 'Assurance-vie', payload: { asOf: '2025-09-30', amount: { amount: '1200.50', currency: 'EUR' } }, sourceLabel: null, createdVia: 'api' }),
    ] });
    const { container } = await renderApp('/proposals');

    const table = await screen.findByRole('table', { name: 'Liste des propositions' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Document', 'Élément', 'Reçue le', 'État']);
    const [first, second] = await rows();
    expect(first).toHaveTextContent('Relevé PEA septembre');
    expect(within(first!).getByRole('link', { name: /PEA — valeur au 30 sept\. 2025 : 23\s500,00\s€/ })).toBeInTheDocument();
    expect(first).toHaveTextContent('par l’assistant (MCP)');
    expect(first).toHaveTextContent('En attente');
    expect(second).toHaveTextContent('Sans relevé');
    expect(second).toHaveTextContent('Assurance-vie');
    expect(second).toHaveTextContent('par l’API');
    await expectNoAxeViolations(container);
  });

  it('lists the newest proposal first and sorts by the date received', async () => {
    serve({ pending: [
      proposal({ sourceLabel: 'Relevé ancien', createdAt: '2026-10-01T08:00:00+00:00' }),
      proposal({ sourceLabel: 'Relevé récent', createdAt: '2026-10-10T08:00:00+00:00' }),
    ] });
    const { user } = await renderApp('/proposals');

    const documents = async () => (await rows()).map((r) => within(r).getAllByRole('cell')[0]!.textContent);
    expect(await documents()).toEqual(['Relevé récent', 'Relevé ancien']);
    await user.click(within(screen.getByRole('table', { name: 'Liste des propositions' })).getByRole('button', { name: 'Reçue le' }));
    expect(await documents()).toEqual(['Relevé ancien', 'Relevé récent']);
  });

  it('says when nothing waits, and shows the decided ones on their tab', async () => {
    serve({ approved: [proposal({ status: 'approved', decidedAt: '2026-10-16T09:00:00+00:00' })] });
    const { user } = await renderApp('/proposals');

    expect(await screen.findByText('Rien à valider pour l’instant.')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'En attente' })).toHaveAttribute('aria-selected', 'true');
    await user.click(screen.getByRole('tab', { name: 'Approuvées' }));
    expect((await inTable()).getByText(/Approuvée le/)).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Approuvées' })).toHaveAttribute('aria-selected', 'true');
    await user.click(screen.getByRole('tab', { name: 'Rejetées' }));
    expect(await screen.findByText('Aucune proposition rejetée.')).toBeInTheDocument();
  });

  // Review Focus 1.
  it('shows a hostile label and note as plain text', async () => {
    serve({ pending: [proposal({ sourceLabel: '<img src=x onerror=alert(1)> **gras**' })] });
    const { container } = await renderApp('/proposals');

    expect((await rows())[0]).toHaveTextContent('<img src=x onerror=alert(1)> **gras**');
    expect(container.querySelector('img')).toBeNull();
  });

  // Review Focus 5.
  it('an unknown kind is listed under its raw name', async () => {
    serve({ pending: [proposal({ kind: 'realestate.future_kind', payload: { x: 1 } })] });
    await renderApp('/proposals');

    expect((await inTable()).getByRole('link', { name: /realestate\.future_kind/ })).toBeInTheDocument();
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

    expect((await inTable()).getByRole('link', { name: /PEA — relevé au 30 sept\. 2025 \(2 mouvements\)/ })).toBeInTheDocument();
  });

  it('summarises a payslip and an invoice', async () => {
    serve({ pending: [
      proposal({ kind: 'income.change_pay', targetName: 'Salaire', payload: { paidOn: '2025-09-28', phaseLabel: 'Salaire', net: { amount: '2400.00', currency: 'EUR' }, effectiveOn: '2025-09-01' } }),
      proposal({ kind: 'subscriptions.change_price', targetName: 'Netflix', payload: { chargedOn: '2025-09-01', amount: { amount: '10.99', currency: 'EUR' } } }),
    ] });
    await renderApp('/proposals');

    expect((await inTable()).getByRole('link', { name: /Salaire — bulletin du 28 sept\. 2025 : net 2\s400,00\s€/ })).toBeInTheDocument();
    expect((await inTable()).getByRole('link', { name: /Netflix — facture du 1 sept\. 2025 : 10,99\s€/ })).toBeInTheDocument();
  });

  it('summarises a bank’s table by its loan and its row count', async () => {
    serve({ pending: [proposal({ kind: 'realestate.import_amortization', targetName: 'Prêt maison', payload: { rows: [
      { dueDate: '2020-11-15', payment: '1159.92', interest: '583.33', principal: '576.59', crd: '199423.41' },
    ] } })] });
    await renderApp('/proposals');

    expect((await inTable()).getByRole('link', { name: /Prêt maison — tableau de la banque, 1 échéance/ })).toBeInTheDocument();
  });
});
