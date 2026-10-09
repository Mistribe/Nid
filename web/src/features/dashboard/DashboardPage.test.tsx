import { invalidateInvestments } from '../investments/api';
import { screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { DashboardView } from './api';
import { dashboard, emptyDashboard, figure } from './testData';

function serve(body: DashboardView, seen: string[] = []) {
  server.use(http.get('*/api/v1/dashboard', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(body); }));
}

const card = (name: string) => screen.findByRole('region', { name });

describe('DashboardPage', () => {
  it('shows the four figures the server computed', async () => {
    serve(dashboard());
    const { container } = await renderApp('/dashboard');

    const worth = await card('Patrimoine');
    expect(worth).toHaveTextContent('12 900,00 €');
    expect(worth).toHaveTextContent('Valeurs au 30 septembre 2025');
    const commitments = await card('Engagements');
    expect(commitments).toHaveTextContent('250,56 €');
    expect(commitments).toHaveTextContent('120,00 € prélevés ce mois-ci');
    expect(commitments).toHaveTextContent('10 % des revenus');
    const income = await card('Revenus et marge');
    expect(income).toHaveTextContent('2 500,00 €');
    expect(income).toHaveTextContent('Marge avant dépenses courantes : 2 249,45 €');
    const change = await card('Évolution depuis le 1er janvier');
    expect(change).toHaveTextContent('+1 000,00 €');
    expect(change).toHaveTextContent('+9,1 %');
    expect(within(change).getByText('en hausse')).toBeInTheDocument();
    expect(change).toHaveTextContent('dont 1 000,00 € versés');
    await expectNoAxeViolations(container);
  });

  it('says « inconnu » with its reason, never 0', async () => {
    serve(dashboard({
      netWorth: figure(null), valuesAsOf: null, envelopes: 1, envelopesWithoutValue: 1,
      income: figure(null), incomeSources: 0, margin: null, committedPct: null,
      change: figure(null), changeContributions: figure(null), changePct: null,
    }));
    const { container } = await renderApp('/dashboard');

    const worth = await card('Patrimoine');
    expect(worth).toHaveTextContent('Inconnu : aucune valeur saisie');
    expect(worth).toHaveTextContent('1 enveloppe sans valeur');
    expect(worth).not.toHaveTextContent('0,00 €');
    const income = await card('Revenus et marge');
    expect(income).toHaveTextContent('Inconnu : aucun revenu déclaré');
    expect(income).toHaveTextContent('Marge inconnue tant qu’aucun revenu n’est déclaré');
    expect(await card('Engagements')).not.toHaveTextContent('% des revenus');
    expect(await card('Évolution depuis le 1er janvier')).toHaveTextContent('Inconnue : aucune valeur au 1er janvier');
    await expectNoAxeViolations(container);
  });

  it('names the currency whose rate is missing', async () => {
    serve(dashboard({ netWorth: figure(null, { perCurrency: [{ amount: '1000.00', currency: 'USD' }], missingCurrency: 'USD' }) }));
    await renderApp('/dashboard');

    expect(await card('Patrimoine')).toHaveTextContent('Inconnu : taux de change manquant (USD)');
  });

  it('flags stale values in words, and a loss as « en baisse »', async () => {
    serve(dashboard({ staleEnvelopes: 2, change: figure('-500.00'), changePct: -4.5 }));
    await renderApp('/dashboard');

    expect(await card('Patrimoine')).toHaveTextContent('2 valeurs à mettre à jour');
    const change = await card('Évolution depuis le 1er janvier');
    expect(change).toHaveTextContent('-500,00 €');
    expect(within(change).getByText('en baisse')).toBeInTheDocument();
  });

  it('says « Aucun coût ce mois-ci » for subscriptions without a cost this month', async () => {
    serve(dashboard({ commitments: figure(null), cashThisMonth: figure(null), committedPct: null, subscriptions: 2 }));
    await renderApp('/dashboard');

    const commitments = await card('Engagements');
    expect(commitments).toHaveTextContent('Aucun coût ce mois-ci');
    expect(commitments).not.toHaveTextContent('Aucun abonnement');
  });

  it('names the missing rate when the margin is unknown though income is declared', async () => {
    serve(dashboard({ commitments: figure(null, { missingCurrency: 'GBP' }), margin: null, committedPct: null }));
    await renderApp('/dashboard');

    expect(await card('Revenus et marge')).toHaveTextContent('Marge inconnue : taux de change manquant (GBP)');
  });

  it('says the margin is unknown without income, even when a commitment misses a rate', async () => {
    serve(dashboard({ income: figure(null), incomeSources: 0, commitments: figure(null, { missingCurrency: 'GBP' }), margin: null, committedPct: null }));
    await renderApp('/dashboard');

    const income = await card('Revenus et marge');
    expect(income).toHaveTextContent('Marge inconnue tant qu’aucun revenu n’est déclaré');
    expect(income).not.toHaveTextContent('taux de change manquant');
  });

  it('names the missing rate on the change card in the feminine', async () => {
    serve(dashboard({ change: figure(null, { missingCurrency: 'USD' }), changePct: null }));
    await renderApp('/dashboard');

    expect(await card('Évolution depuis le 1er janvier')).toHaveTextContent('Inconnue : taux de change manquant (USD)');
  });

  it('refetches after a write', async () => {
    const seen: string[] = [];
    serve(dashboard(), seen);
    const { queryClient } = await renderApp('/dashboard');
    await card('Patrimoine');

    await invalidateInvestments(queryClient);

    await waitFor(() => expect(seen).toHaveLength(2));
  });

  it('welcomes a household with nothing yet, with the three ways to start', async () => {
    serve(emptyDashboard);
    const { container } = await renderApp('/dashboard');

    expect(await screen.findByRole('heading', { name: 'Bienvenue dans Nid' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Ajouter une enveloppe' })).toHaveAttribute('href', '/investments/new');
    expect(screen.getByRole('link', { name: 'Ajouter un abonnement' })).toHaveAttribute('href', '/subscriptions/new');
    expect(screen.getByRole('link', { name: 'Ajouter un revenu' })).toHaveAttribute('href', '/income/new');
    await expectNoAxeViolations(container);
  });

  it('shows skeletons while loading', async () => {
    server.use(http.get('*/api/v1/dashboard', async () => { await delay('infinite'); return HttpResponse.json(dashboard()); }));
    await renderApp('/dashboard');
    expect(await screen.findByLabelText('Chargement du tableau de bord')).toHaveAttribute('aria-busy', 'true');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/dashboard');
    expect(await screen.findByRole('alert', {}, { timeout: 8000 })).toBeInTheDocument();
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve(dashboard(), seen);
    await renderApp('/dashboard?scope=me');

    await card('Patrimoine');
    expect(seen).toEqual(['?scope=me']);
  });

  it('is the home page, first in the navigation', async () => {
    serve(dashboard());
    const { router } = await renderApp('/');

    await card('Patrimoine');
    expect(router.state.location.pathname).toBe('/dashboard');
    expect(screen.getAllByRole('link', { name: 'Accueil' }).length).toBeGreaterThan(0);
  });

  it('says how many envelopes have no recent value at 1 January', async () => {
    serve(dashboard({ envelopesWithoutStart: 2 }));
    await renderApp('/dashboard');
    expect(await card('Évolution depuis le 1er janvier')).toHaveTextContent('2 enveloppes sans valeur récente au 1er janvier');
  });
});
