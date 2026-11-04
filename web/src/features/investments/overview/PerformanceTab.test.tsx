import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { HouseholdPerformance } from '../api';
import { householdPerformance, overview, PEA, performance } from '../testData';

const serve = (body: HouseholdPerformance, seen: string[] = []) =>
  server.use(http.get('*/api/v1/investments/overview/performance', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
const region = (name: string) => screen.findByRole('region', { name });

describe('Placements › performance', () => {
  beforeAll(async () => { await import('../EnvelopesPage'); await import('./PerformanceTab'); }, 30_000);
  // The presets are dates from the browser's today.
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2025, 9, 15, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('opens on this year and shows the server figures', async () => {
    const seen: string[] = [];
    serve(householdPerformance(), seen);
    const { container } = await renderApp('/investments?tab=performance');

    const figures = (await screen.findByText('Performance de la période')).closest('dl')!;
    expect(figures).toHaveTextContent('+1 840,00 €en haussehors versements · avant fiscalité');
    expect(figures).toHaveTextContent('Rapporté aux sommes engagées+5,5 %');
    expect(figures).toHaveTextContent('Meilleur mois+1 510,00 €mars 2025');
    expect(figures).toHaveTextContent('Pire mois-310,00 €février 2025');
    expect(seen).toEqual(['?scope=household&from=2025-01-01&to=2025-10-15']);
    expect(screen.getByRole('radio', { name: 'Cette année' })).toBeChecked();
    expect(screen.getByText('1 enveloppe ne compte pas : CTO (pas de valeur au début de la période).')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows the performance with its arrow, the figure uncoloured', async () => {
    serve(householdPerformance());
    await renderApp('/investments?tab=performance');

    const performance = (await screen.findByText('Performance de la période')).nextElementSibling!;
    expect(performance.className).not.toMatch(/text-status-good-text|text-destructive/);
    expect(performance.querySelector('svg')).toHaveClass('text-status-good');
    expect(within(performance as HTMLElement).getByText('en hausse')).toBeInTheDocument();
  });

  it('decomposes the change in value and says what earned it', async () => {
    serve(householdPerformance());
    await renderApp('/investments?tab=performance');

    const bridge = await screen.findByRole('table', { name: 'Variation de valeur de la période' });
    const lines = ['Valeur au 1 janv. 202531 000,00 €', 'Versements+2 400,00 €', 'Retraits−240,00 €', 'Performance+1 840,00 €', 'Valeur au 15 oct. 202535 000,00 €'];
    within(bridge).getAllByRole('row').forEach((row, i) => expect(row).toHaveTextContent(lines[i]!));
    const earned = await region('Ce qui a rapporté');
    expect(within(earned).getByRole('link', { name: 'PEA' })).toHaveAttribute('href', `/investments/${PEA}`);
    expect(earned).toHaveTextContent('PEA+1 700,00 €');
    expect(await region('Rendement annuel depuis l’ouverture')).toHaveTextContent('Vos livrets +2,6 %');
  });

  it('keeps a preset in the URL, offers « Tout » from the oldest value, and takes a free period', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/investments/overview', () => HttpResponse.json(overview({ firstAsOf: '2024-12-31' }))));
    serve(householdPerformance(), seen);
    const { user, router } = await renderApp('/investments?tab=performance');

    await user.click(await screen.findByRole('radio', { name: '12 mois' }));
    expect(router.state.location.search).toMatchObject({ from: '2024-10-15', to: '2025-10-15' });
    await user.click(await screen.findByRole('radio', { name: 'Tout' }));
    await waitFor(() => expect(seen).toContain('?scope=household&from=2024-12-31&to=2025-10-15'));

    await user.click(screen.getByRole('button', { name: 'Autre période…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Choisir une période' });
    const from = within(dialog).getByLabelText('Du');
    await user.clear(from);
    await user.type(from, '30/09/2025');
    await user.click(within(dialog).getByRole('button', { name: 'Afficher' }));
    await waitFor(() => expect(seen).toContain('?scope=household&from=2025-09-30&to=2025-10-15'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Autre période…' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('shows the months as a table on demand, the sign in words', async () => {
    serve(householdPerformance());
    const { user } = await renderApp('/investments?tab=performance');

    await user.click(await screen.findByRole('button', { name: 'Afficher en tableau' }));
    const table = screen.getByRole('table', { name: 'Performance de chaque mois' });
    const months = ['janvier 2025+640,00 €', 'février 2025-310,00 €', 'mars 2025+1 510,00 €'];
    within(table).getAllByRole('row').slice(1).forEach((row, i) => expect(row).toHaveTextContent(months[i]!));
  });

  it('without the savings accounts, draws no reference; with nothing counted, says so', async () => {
    serve(performance({ excluded: [] }));
    await renderApp('/investments?tab=performance');

    expect(await screen.findByText('Aucune enveloppe n’a de valeur au début de cette période.')).toBeInTheDocument();
    expect(screen.queryByText(/Vos livrets/)).not.toBeInTheDocument();
  });
});
