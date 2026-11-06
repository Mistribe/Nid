import { render, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { expectLabelBefore } from '@/test/filters';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { IncomeOverview } from '../api';
import { overview, SALARY, sourceRow } from '../testData';
import { Change } from './OverviewFigures';
import '@/locales/income';

const ALICE = '0192f0aa-0000-7000-8000-0000000000a1';

function serve(body: IncomeOverview = overview(), seen: URLSearchParams[] = []) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: ALICE, kind: 'adult', displayName: 'Alice', email: null }])),
    http.get('*/api/v1/income/overview', ({ request }) => {
      seen.push(new URL(request.url).searchParams);
      return HttpResponse.json(body);
    }),
  );
  return seen;
}

describe('Income overview', () => {
  beforeAll(async () => { await import('../IncomeSourcesPage'); }, 30_000);

  it('asks the overview for the year by default and keeps the period in the URL', async () => {
    const seen = serve();
    const { user, router } = await renderApp('/income');

    await screen.findByText('6 420,00 €');
    expect(seen[0]!.get('period')).toBe('year');
    expect(router.state.location.search).not.toHaveProperty('period');

    await user.click(screen.getByRole('radio', { name: '3 ans' }));
    await waitFor(() => expect(seen.at(-1)!.get('period')).toBe('3y'));
    expect(router.state.location.search).toMatchObject({ period: '3y' });
  });

  it('shows the four figures as the server gives them', async () => {
    serve();
    await renderApp('/income');

    expect(await screen.findByText('6 420,00 €')).toBeInTheDocument();
    expect(screen.getByText('77 040,00 €')).toBeInTheDocument();
    expect(screen.getByText('64 200,00 € déjà perçus')).toBeInTheDocument();
    expect(screen.getByText('Total 2026 (projeté)')).toBeInTheDocument();
    expect(screen.getAllByText(/\+4,1 %/).length).toBeGreaterThan(0);
    // The 3-year change has no history: a dash that explains itself.
    expect(screen.getByRole('button', { name: /Pas assez d’historique/ })).toHaveTextContent('—');
  });

  it('leads with today’s monthly income in the hero, then the split, months and sources each in their card (#394)', async () => {
    serve(overview({ sources: [sourceRow()] }));
    await renderApp('/income');

    const hero = await screen.findByRole('region', { name: 'Revenu net / mois' });
    expect(hero).toHaveTextContent('6 420,00 €');
    expect(hero).toHaveTextContent('Aujourd’hui, lissé sur l’année');
    expect(screen.getByText('77 040,00 €').closest('dl')).not.toHaveTextContent('Revenu net / mois');
    // The split's monthly amounts are the period's average, the table's today's smoothed amount: each says which.
    const split = screen.getByRole('region', { name: 'Répartition des revenus' });
    expect(split).toHaveTextContent('Moyenne par mois en 2026');
    expect(split).toHaveTextContent('5 000,00 €');
    const sources = screen.getByRole('region', { name: 'Sources de revenu' });
    expect(within(sources).getByRole('switch', { name: 'Afficher les sources terminées' })).toBeInTheDocument();
    expect(within(sources).getByRole('columnheader', { name: /Par mois, lissé sur l’année/ })).toBeInTheDocument();
  });

  it('gives the change over a year after the household’s inflation, and where that assumption comes from', async () => {
    serve();
    await renderApp('/income');

    expect(await screen.findByText('soit +2,1 % après inflation')).toBeInTheDocument();
    expect(screen.getByText(/Inflation supposée : 2 % par an/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'vos hypothèses de projection' })).toHaveAttribute('href', '/settings/projection');
  });

  it('says nothing of inflation when the change over a year is unknown', async () => {
    serve(overview({ figures: { ...overview().figures, monthlyChange1y: null, monthlyRealChange1y: null } }));
    await renderApp('/income');

    await screen.findByText('6 420,00 €');
    expect(screen.queryByText(/après inflation/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Inflation supposée/)).not.toBeInTheDocument();
  });

  it('filters by person and type', async () => {
    const seen = serve();
    const { user } = await renderApp('/income');

    await user.click(await screen.findByRole('radio', { name: 'Alice' }));
    await waitFor(() => expect(seen.at(-1)!.get('person')).toBe(ALICE));
    await pick(user, 'Type', 'Loyers');
    await waitFor(() => expect(seen.at(-1)!.get('kind')).toBe('rent'));
  });

  it('the income filters sit in one row, each labelled before its control', async () => {
    serve();
    await renderApp('/income');

    const row = await screen.findByRole('group', { name: 'Filtres' });
    expect(within(row).getByRole('radiogroup', { name: 'Période' })).toBeInTheDocument();
    expect(within(row).getByRole('radiogroup', { name: 'Personne' })).toBeInTheDocument();
    expect(within(row).getByRole('combobox', { name: 'Type' })).toBeInTheDocument();
    for (const [label, role] of [['Période', 'radiogroup'], ['Personne', 'radiogroup'], ['Type', 'combobox']] as const) {
      expectLabelBefore(within(row).getByText(label), within(row).getByRole(role, { name: label }));
    }
    const figures = await screen.findByText('77 040,00 €');
    expect(row.compareDocumentPosition(figures.closest('dl')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('isolates a source from the split bar and clears it on a second click', async () => {
    const seen = serve();
    const { user, router } = await renderApp('/income');

    const bar = await screen.findByRole('group', { name: 'Répartition des revenus' });
    expect(within(bar).getByText(/Exceptionnels/)).toBeInTheDocument();
    expect(within(bar).queryByRole('button', { name: /Exceptionnels/ })).toBeNull();
    await user.click(within(bar).getByRole('button', { name: /Salaire Alice/ }));
    await waitFor(() => expect(seen.at(-1)!.get('source')).toBe(SALARY));
    expect(router.state.location.search).toMatchObject({ source: SALARY });
    await user.click(within(bar).getByRole('button', { name: /Salaire Alice/ }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('source'));
  });

  it('labels the neutral shares in words', async () => {
    const [salary] = overview().split;
    serve(overview({ split: [salary!, { item: null, name: 'other', kind: 'other', total: salary!.total, perMonth: salary!.perMonth, percent: 5 }] }));
    await renderApp('/income');

    expect(await screen.findByText(/Autres/)).toBeInTheDocument();
    expect(screen.queryByText(/^other/)).toBeNull();
  });

  it('clears the isolated source when a filter changes', async () => {
    serve();
    const { user, router } = await renderApp('/income?source=' + SALARY);

    await screen.findByText('6 420,00 €');
    await user.click(screen.getByRole('radio', { name: '3 ans' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ period: '3y' }));
    expect(router.state.location.search).not.toHaveProperty('source');
  });

  it('removes the person from the URL on « Tous »', async () => {
    serve();
    const { user, router } = await renderApp('/income?person=' + ALICE);

    await user.click(await screen.findByRole('radio', { name: 'Tous' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('person'));
  });

  it('says why the split is missing when a rate is', async () => {
    serve(overview({ split: [], sources: [sourceRow({ perMonth: null })], missingRates: ['USD'] }));
    await renderApp('/income');

    expect(await screen.findByText('Répartition indisponible : taux manquant (USD)')).toBeInTheDocument();
  });

  it('a figure lost to a missing rate names the currency, not the history', async () => {
    const { figures } = overview();
    serve(overview({ figures: { ...figures, monthlyNow: null, total: null }, missingRates: ['USD'] }));
    await renderApp('/income');

    const dashes = await screen.findAllByRole('button', { name: 'taux manquant (USD)' });
    expect(dashes[0]).toHaveTextContent('—');
    expect(screen.queryByRole('button', { name: /Pas assez d’historique/ })).toBeNull();
  });

  it('has no axe violations', async () => {
    serve();
    const { container } = await renderApp('/income');

    await screen.findByText('6 420,00 €');
    await expectNoAxeViolations(container);
  });
});

describe('Change', () => {
  it('shows its arrow in the status colour and says its direction', () => {
    const { container, rerender } = render(<Change value={0.042} />);
    expect(container.querySelector('svg')).toHaveClass('text-status-good');
    expect(screen.getByText('hausse')).toBeInTheDocument();
    rerender(<Change value={-0.01} />);
    expect(container.querySelector('svg')).toHaveClass('text-status-critical');
    expect(screen.getByText('baisse')).toBeInTheDocument();
  });

  it('a zero change shows no arrow', () => {
    const { container } = render(<Change value={0} />);
    expect(container.querySelector('svg')).toBeNull();
    expect(container.innerHTML).not.toMatch(/text-status/);
  });
});
