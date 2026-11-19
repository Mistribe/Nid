import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { month, spendingServer } from './testData';

describe('« Dépenses »', () => {
  beforeAll(async () => { await import('./SpendingPage'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 10, 19, 12)); });
  afterEach(() => vi.useRealTimers());

  it('shows the server figures as they come, children folded, « Non classées » last', async () => {
    const asked = spendingServer();
    const { container, user } = await renderApp('/spending');

    const table = await screen.findByRole('table', { name: 'Par catégorie' });
    expect(new URLSearchParams(asked[0]).toString()).toBe('scope=household&month=2026-11');
    const figures = screen.getByText('Dépensé').closest('div')!;
    expect(figures).toHaveTextContent(/1\s234,56\s€/);
    expect(screen.getByText('Mois habituel', { selector: 'dt' }).closest('div')).toHaveTextContent(/1\s100,00\s€/);
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((r) => within(r).getAllByRole('cell')[0]!.textContent)).toEqual(['Courses', 'Loisirs', 'Non classées']);
    expect(rows[0]).toHaveTextContent(/640,10\s€/);
    expect(rows[1]).toHaveTextContent('—');
    expect(within(table).queryByText('Supermarché')).toBeNull();

    await user.click(within(rows[0]!).getByRole('button', { name: 'Afficher les sous-catégories de Courses' }));
    expect(within(table).getByText('Supermarché')).toBeInTheDocument();
    expect(screen.queryByRole('note')).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('reads the scope from the address', async () => {
    const asked = spendingServer();
    await renderApp('/spending?scope=me');
    await screen.findByRole('table', { name: 'Par catégorie' });
    expect(asked[0]).toContain('scope=me');
  });

  it('steps through the months and the 12 months up to one', async () => {
    const asked = spendingServer();
    const { user, router } = await renderApp('/spending');
    await screen.findByRole('table', { name: 'Par catégorie' });

    const next = screen.getByRole('button', { name: 'Mois suivant' });
    expect(next).toHaveAttribute('aria-disabled', 'true');
    await user.click(screen.getByRole('button', { name: 'Mois précédent' }));
    await waitFor(() => expect(asked.at(-1)).toContain('month=2026-10'));
    expect(router.state.location.search).toMatchObject({ month: '2026-10' });
    expect(screen.getByRole('group', { name: 'Mois' })).toHaveTextContent('octobre 2026');

    await user.click(screen.getByRole('radio', { name: '12 derniers mois' }));
    await waitFor(() => expect(new URLSearchParams(asked.at(-1)).toString()).toBe('scope=household&from=2025-11&to=2026-10'));
    expect(router.state.location.search).toMatchObject({ month: '2026-10', period: '12m' });
  });

  it('marks a total left incomplete by a missing rate', async () => {
    spendingServer({ ...month, incomplete: true });
    await renderApp('/spending');
    expect(await screen.findByRole('note')).toHaveTextContent('Certaines opérations en devise n’ont pas de taux : total incomplet.');
  });

  it('invites to turn bank sync on, then to link an account', async () => {
    await renderApp('/spending');
    expect(await screen.findByRole('heading', { name: 'La banque n’est pas activée' })).toBeInTheDocument();

    server.use(
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: true })),
      http.get('*/api/v1/banking/accounts', () => HttpResponse.json([])),
    );
    await renderApp('/spending');
    expect(await screen.findByRole('heading', { name: 'Aucun compte bancaire relié' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Relier une banque' })).toHaveAttribute('href', '/settings/banks');
  });

  it('points to the Opérations tab while nothing is categorised, the unclassified lines still listed', async () => {
    spendingServer({ ...month, categories: [] });
    const { container } = await renderApp('/spending');
    expect(await screen.findByRole('heading', { name: 'Aucune opération classée' })).toBeInTheDocument();
    const rows = within(screen.getByRole('table', { name: 'Par catégorie' })).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toHaveTextContent('Non classées');
    await expectNoAxeViolations(container);
  });
});
