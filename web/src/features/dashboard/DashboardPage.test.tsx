import { invalidateInvestments } from '@/api/investments';
import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { delay } from 'msw/utils/delay';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { DashboardView } from '@/api/dashboard';
import { eur, overview, SALARY } from '../income/testData';
import { curvePoint, dashboard, emptyDashboard, figure, netWorth, todoItem } from './testData';

function serve(body: DashboardView, seen: string[] = []) {
  server.use(http.get('*/api/v1/dashboard', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(body); }));
}

const tile = (name: string) => screen.findByRole('button', { name: new RegExp(name) });

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./NetWorthCurve'); }, 30_000);

describe('DashboardPage', () => {
  it('leads to the annual review', async () => {
    serve(dashboard());
    await renderApp('/dashboard?scope=me');

    expect(await screen.findByRole('link', { name: 'Bilan annuel' })).toHaveAttribute('href', '/reports/year?scope=me');
  });

  it('labels the net worth total « estimé » only when an estimate is in it', async () => {
    serve(dashboard({ estimatedEnvelopes: 1 }));
    const { unmount } = await renderApp('/dashboard');
    expect(await tile('Patrimoine net')).toHaveTextContent('12 900,00 € estimé');
    unmount();
    serve(dashboard());
    await renderApp('/dashboard');
    expect(await tile('Patrimoine net')).not.toHaveTextContent('estimé');
  });

  it('shows the four key figures in one row', async () => {
    serve(dashboard());
    const { container, router, user } = await renderApp('/dashboard');

    const worth = await tile('Patrimoine net');
    expect(worth).toHaveTextContent('12 900,00 €');
    expect(worth).toHaveTextContent('+1 000,00 €');
    expect(worth).toHaveTextContent('+9,1 %');
    expect(worth).toHaveTextContent('en hausse');
    expect(worth).toHaveTextContent('depuis le 1er janvier');
    expect(await tile('Revenus nets / mois')).toHaveTextContent('2 500,00 €');
    const commitments = await tile('Engagements / mois');
    expect(commitments).toHaveTextContent('250,56 €');
    expect(commitments).toHaveTextContent('10 % des revenus');
    const margin = await tile('Reste à vivre');
    expect(margin).toHaveTextContent('2 249,45 €');
    expect(margin).toHaveTextContent('avant dépenses courantes');
    await expectNoAxeViolations(container);

    await user.click(worth);
    expect(router.state.location.search).toEqual({ panel: 'net_worth' });
  });

  it('says « inconnu » with its reason, never 0', async () => {
    serve(dashboard({
      netWorth: figure(null), valuesAsOf: null, envelopes: 1, envelopesWithoutValue: 1,
      income: figure(null), incomeSources: 0, margin: null, committedPct: null,
      change: figure(null), changeContributions: figure(null), changePct: null,
    }));
    const { container } = await renderApp('/dashboard');

    const worth = await tile('Patrimoine net');
    expect(worth).toHaveTextContent('Inconnu : aucune valeur saisie');
    expect(worth).toHaveTextContent('Inconnue : aucune valeur au 1er janvier');
    expect(worth).not.toHaveTextContent('0,00 €');
    expect(await tile('Revenus nets / mois')).toHaveTextContent('Inconnu : aucun revenu déclaré');
    expect(await tile('Reste à vivre')).toHaveTextContent('Marge inconnue tant qu’aucun revenu n’est déclaré');
    expect(await tile('Engagements / mois')).not.toHaveTextContent('% des revenus');
    await expectNoAxeViolations(container);
  });

  it('names the currency whose rate is missing', async () => {
    serve(dashboard({ netWorth: figure(null, { perCurrency: [{ amount: '1000.00', currency: 'USD' }], missingCurrency: 'USD' }) }));
    await renderApp('/dashboard');

    expect(await tile('Patrimoine net')).toHaveTextContent('Inconnu : taux de change manquant (USD)');
  });

  it('shows a loss as « en baisse »', async () => {
    serve(dashboard({ change: figure('-500.00'), changePct: -4.5 }));
    await renderApp('/dashboard');

    const worth = await tile('Patrimoine net');
    expect(worth).toHaveTextContent('-500,00 €');
    expect(within(worth).getByText('en baisse')).toBeInTheDocument();
  });

  it('names the items the change since 1 January leaves out', async () => {
    serve(dashboard({ change: figure('-500.00'), changePct: null, envelopesWithoutStart: 2 }));
    await renderApp('/dashboard');

    const worth = await tile('Patrimoine net');
    expect(worth).toHaveTextContent('depuis le 1er janvier');
    expect(worth).toHaveTextContent('2 éléments sans valeur récente au 1er janvier');
  });

  it('says « Aucun coût ce mois-ci » when a source costs nothing this month', async () => {
    serve(dashboard({
      commitments: figure(null), cashThisMonth: figure(null), committedPct: null, subscriptions: 0,
      commitmentSources: [{ source: 'loans', amortized: { amount: '0.00', currency: 'EUR' }, estimated: false }],
    }));
    await renderApp('/dashboard');

    const commitments = await tile('Engagements / mois');
    expect(commitments).toHaveTextContent('Aucun coût ce mois-ci');
    expect(commitments).not.toHaveTextContent('Aucun engagement');
  });

  it('says « Aucun engagement » with no source at all', async () => {
    serve(dashboard({ commitments: figure(null), cashThisMonth: figure(null), committedPct: null, subscriptions: 0, commitmentSources: [] }));
    await renderApp('/dashboard');

    expect(await tile('Engagements / mois')).toHaveTextContent('Aucun engagement');
  });

  it('names the missing rate when the margin is unknown though income is declared', async () => {
    serve(dashboard({ commitments: figure(null, { missingCurrency: 'GBP' }), margin: null, committedPct: null }));
    await renderApp('/dashboard');

    expect(await tile('Reste à vivre')).toHaveTextContent('Marge inconnue : taux de change manquant (GBP)');
  });

  it('says the margin is unknown without income, even when a commitment misses a rate', async () => {
    serve(dashboard({ income: figure(null), incomeSources: 0, commitments: figure(null, { missingCurrency: 'GBP' }), margin: null, committedPct: null }));
    await renderApp('/dashboard');

    const margin = await tile('Reste à vivre');
    expect(margin).toHaveTextContent('Marge inconnue tant qu’aucun revenu n’est déclaré');
    expect(margin).not.toHaveTextContent('taux de change manquant');
  });

  it('names the missing rate on the change in the feminine', async () => {
    serve(dashboard({ change: figure(null, { missingCurrency: 'USD' }), changePct: null }));
    await renderApp('/dashboard');

    expect(await tile('Patrimoine net')).toHaveTextContent('Inconnue : taux de change manquant (USD)');
  });

  it('splits the month into its sources', async () => {
    serve(dashboard({
      income: figure('4100.00'), margin: { amount: '1185.00', currency: 'EUR' },
      commitments: figure('2605.00'), cashThisMonth: figure('120.00'),
      commitmentSources: [
        { source: 'subscriptions', amortized: { amount: '125.00', currency: 'EUR' }, estimated: false },
        { source: 'loans', amortized: { amount: '2250.00', currency: 'EUR' }, estimated: false },
        { source: 'property_costs', amortized: { amount: '230.00', currency: 'EUR' }, estimated: false },
        { source: 'tax', amortized: { amount: '310.00', currency: 'EUR' }, estimated: false },
      ],
    }));
    const { container } = await renderApp('/dashboard');

    const month = await screen.findByRole('region', { name: 'Où part le revenu du mois' });
    expect(month).toHaveTextContent('120,00 € prélevés ce mois-ci');
    const labels = within(within(month).getByRole('list', { name: 'Où il part' })).getAllByRole('listitem').map((li) => li.textContent?.replace(/[\u00a0\u202f]/g, ' '));
    expect(labels).toEqual(['Abonnements 125,00 €', 'Crédits 2 250,00 €', 'Charges des biens 230,00 €', 'Impôts estimés 310,00 €', 'Reste 1 185,00 €']);
    const widths = Array.from(month.querySelectorAll<HTMLElement>('[data-segment]')).map((el) => el.style.flexGrow);
    expect(widths).toEqual(['125', '2250', '230', '310', '1185']);
    await expectNoAxeViolations(container);
  });

  it('draws the flow from today\'s income split to the commitments and the rest, each with its share', async () => {
    serve(dashboard({ marginPct: 90, commitmentSources: [{ source: 'subscriptions', amortized: eur('250.56'), pct: 10, estimated: false }] }));
    const PROPERTY = '0192f0aa-0000-7000-8000-00000000d001';
    server.use(http.get('*/api/v1/income/overview', () => HttpResponse.json(overview({
      nowSplit: [
        { item: SALARY, module: 'income', name: 'Salaire Alice', kind: 'salary', perMonth: eur('2000.00'), percent: 80 },
        { item: PROPERTY, module: 'realestate', name: 'Studio Lyon', kind: 'rent', perMonth: eur('400.00'), percent: 16 },
        { item: null, module: null, name: 'other', kind: 'other', perMonth: eur('100.00'), percent: 4 },
      ],
    }))));
    const { container, user } = await renderApp('/dashboard');

    const month = await screen.findByRole('region', { name: 'Où part le revenu du mois' });
    const flow = within(month).getByRole('group', { name: 'Flux du revenu du mois vers les engagements et le reste' });
    expect(await within(flow).findByRole('link', { name: /Salaire Alice/ })).toHaveAttribute('href', `/income/${SALARY}`);
    expect(flow).toHaveTextContent('2 500,00 € / mois');
    // Each label column is as wide as the drawing (issue 361): the names have room before they truncate.
    expect(flow.querySelector('[data-sankey-columns]')).toHaveClass('grid-cols-[minmax(0,1fr)_minmax(10rem,1fr)_minmax(0,1fr)]');
    expect(within(flow).getByRole('link', { name: /Studio Lyon/ })).toHaveAttribute('href', `/properties/${PROPERTY}?tab=rental`);
    expect(Array.from(flow.querySelectorAll('[data-band]')).map((el) => el.getAttribute('data-band')))
      .toEqual([SALARY, PROPERTY, 'other', 'subscriptions', 'rest']);
    expect(flow.textContent?.replace(/[\u00a0\u202f]/g, ' ')).toContain('Reste 2 249,45 € · 90 %');
    const sources = within(within(month).getByRole('list', { name: 'D’où il vient' })).getAllByRole('listitem').map((li) => li.textContent?.replace(/[\u00a0\u202f]/g, ' '));
    expect(sources).toEqual(['Salaire Alice 2 000,00 € · 80 %', 'Studio Lyon 400,00 € · 16 %', 'Autres 100,00 € · 4 %']);
    await expectNoAxeViolations(container);

    await user.hover(flow.querySelector('[data-band="subscriptions"]')!);
    const tip = within(month).getByRole('tooltip');
    expect(tip.textContent?.replace(/[\u00a0\u202f]/g, ' ')).toBe('AbonnementsPar mois250,56 €Part du revenu10 %');
    await user.unhover(flow.querySelector('[data-band="subscriptions"]')!);
    expect(within(month).queryByRole('tooltip')).toBeNull();

    await user.click(within(month).getByRole('button', { name: 'Afficher en tableau' }));
    const rows = within(within(month).getByRole('table', { name: 'Revenu du mois en tableau' })).getAllByRole('row').map((r) => r.textContent?.replace(/[\u00a0\u202f]/g, ' '));
    expect(rows).toEqual([
      'D’où il vientPar moisPart du revenu', 'Salaire Alice2 000,00 €80 %', 'Studio Lyon400,00 €16 %', 'Autres100,00 €4 %',
      'Où il partPar moisPart du revenu', 'Abonnements250,56 €10 %', 'Reste2 249,45 €90 %',
    ]);
  });

  it('draws the home income as one block without the income sources', async () => {
    serve(dashboard());
    await renderApp('/dashboard');
    const month = await screen.findByRole('region', { name: 'Où part le revenu du mois' });
    expect(within(month).getByRole('list', { name: 'D’où il vient' })).toHaveTextContent(/^Revenus 2 500,00/);
    expect(within(month).getByRole('group', { name: /Flux du revenu/ })).toBeInTheDocument();
  });

  it('keeps the bars and draws no flow when commitments exceed the income', async () => {
    serve(dashboard({ margin: { amount: '-300.00', currency: 'EUR' } }));
    await renderApp('/dashboard');
    const month = await screen.findByRole('region', { name: 'Où part le revenu du mois' });
    expect(within(month).getByRole('list', { name: 'Où il part' })).toBeInTheDocument();
    expect(within(month).queryByRole('group', { name: /Flux du revenu/ })).toBeNull();
  });

  it('draws no segment for a source without an amortized amount, nor for an unknown income', async () => {
    serve(dashboard({
      commitmentSources: [{ source: 'subscriptions', amortized: null, estimated: false }, { source: 'loans', amortized: { amount: '100.00', currency: 'EUR' }, estimated: false }],
    }));
    await renderApp('/dashboard');
    const labels = within(within(await screen.findByRole('region', { name: 'Où part le revenu du mois' })).getByRole('list', { name: 'Où il part' })).getAllByRole('listitem').map((li) => li.textContent?.replace(/[\u00a0\u202f]/g, ' '));
    expect(labels).toEqual(['Crédits 100,00 €', 'Reste 2 249,45 €']);
  });

  it('shows the margin reason instead of a bar that looks complete when only the margin is unknown', async () => {
    serve(dashboard({ commitments: figure(null, { missingCurrency: 'GBP' }), margin: null, committedPct: null, commitmentSources: [] }));
    await renderApp('/dashboard');
    const month = await screen.findByRole('region', { name: 'Où part le revenu du mois' });
    expect(month).toHaveTextContent('Marge inconnue : taux de change manquant (GBP)');
    expect(month).not.toHaveTextContent('aucun revenu déclaré');
    expect(month.querySelector('[data-segment]')).toBeNull();
  });

  it('shows the plain margin reason when income is known and no rate is missing', async () => {
    serve(dashboard({ margin: null, committedPct: null }));
    await renderApp('/dashboard');
    const month = await screen.findByRole('region', { name: 'Où part le revenu du mois' });
    expect(month).toHaveTextContent('Marge inconnue tant qu’aucun revenu n’est déclaré');
    expect(month.querySelector('[data-segment]')).toBeNull();
  });

  it('writes a negative margin in its legend without drawing a segment for it', async () => {
    serve(dashboard({ margin: { amount: '-300.00', currency: 'EUR' } }));
    await renderApp('/dashboard');
    const month = await screen.findByRole('region', { name: 'Où part le revenu du mois' });
    expect(month).toHaveTextContent(/Reste -300,00/);
    expect(Array.from(month.querySelectorAll<HTMLElement>('[data-segment]')).map((el) => el.style.flexGrow)).toEqual(['250.56']);
  });

  it('says the month is unknown without income', async () => {
    serve(dashboard({ income: figure(null), incomeSources: 0, margin: null, committedPct: null }));
    await renderApp('/dashboard');
    const month = await screen.findByRole('region', { name: 'Où part le revenu du mois' });
    expect(month).toHaveTextContent('Inconnu : aucun revenu déclaré');
    expect(within(month).queryAllByRole('listitem')).toHaveLength(0);
  });

  it('shows one notice line when something is due', async () => {
    serve(dashboard({ todo: [todoItem(), todoItem({ kind: 'no_value', id: '00000000-0000-7000-8000-0000000000e5', name: 'PER', on: null })] }));
    await renderApp('/dashboard');

    expect(await screen.findByRole('link', { name: '2 choses à faire' })).toHaveAttribute('href', '/routine');
  });

  const gap = (id: string, name: string) => todoItem({ kind: 'income_gap_soon', module: 'income', id, name, on: '2025-12-31' });

  it('counts only the stale and missing values in the routine link', async () => {
    serve(dashboard({ todo: [todoItem(), gap('00000000-0000-7000-8000-0000000000f1', 'Salaire')] }));
    await renderApp('/dashboard');

    expect(await screen.findByRole('link', { name: '1 chose à faire' })).toHaveAttribute('href', '/routine');
    expect(screen.getByRole('link', { name: 'Renseigner la suite de 1 revenu' })).toHaveAttribute('href', '/income/00000000-0000-7000-8000-0000000000f1');
  });

  it('offers only the income link when the household has just a gap', async () => {
    serve(dashboard({ todo: [gap('00000000-0000-7000-8000-0000000000f1', 'Salaire'), gap('00000000-0000-7000-8000-0000000000f2', 'Loyer')] }));
    await renderApp('/dashboard');

    expect(await screen.findByRole('link', { name: 'Renseigner la suite de 2 revenus' })).toHaveAttribute('href', '/income');
    expect(screen.queryByRole('link', { name: /à faire/ })).not.toBeInTheDocument();
  });

  it('counts the expected documents in one line to the documents page (#414)', async () => {
    serve(dashboard());
    const one = { subject: 'property', targetKind: 'core.item', targetId: '00000000-0000-7000-8000-0000000000f1', label: 'Maison', module: 'realestate', role: 'deed', period: 0 };
    server.use(http.get('*/api/v1/documents/expected', () => HttpResponse.json({ items: [one, { ...one, subject: 'loan', role: 'amortization_table' }, { ...one, subject: 'tax_year', period: 2025 }] })));
    await renderApp('/dashboard');

    expect(await screen.findByRole('link', { name: '3 documents attendus' })).toHaveAttribute('href', '/documents');
  });

  it('shows no notice when nothing is due', async () => {
    serve(dashboard());
    await renderApp('/dashboard');
    await tile('Patrimoine net');
    expect(screen.queryByRole('link', { name: /à faire/ })).not.toBeInTheDocument();
  });

  it('refetches after a write', async () => {
    const seen: string[] = [];
    serve(dashboard(), seen);
    const { queryClient } = await renderApp('/dashboard');
    await tile('Patrimoine net');

    await invalidateInvestments(queryClient);

    await waitFor(() => expect(seen).toHaveLength(2));
  });

  it('welcomes a household with nothing yet, with the four ways to start', async () => {
    serve(emptyDashboard);
    const { container } = await renderApp('/dashboard');

    expect(await screen.findByRole('heading', { name: 'Bienvenue dans Nid' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Ajouter une enveloppe' })).toHaveAttribute('href', '/investments/new');
    expect(screen.getByRole('link', { name: 'Ajouter un bien' })).toHaveAttribute('href', '/properties/new');
    expect(screen.getByRole('link', { name: 'Ajouter un abonnement' })).toHaveAttribute('href', '/subscriptions/new');
    expect(screen.getByRole('link', { name: 'Ajouter un revenu' })).toHaveAttribute('href', '/income/new');
    await expectNoAxeViolations(container);
  });

  it('offers no link to a module that is off in the welcome', async () => {
    serve(emptyDashboard);
    await renderApp('/dashboard', { capabilities: { ...allCapabilities, modules: ['core', 'subscriptions', 'investments', 'income'] } });

    expect(await screen.findByRole('link', { name: 'Ajouter une enveloppe' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Ajouter un bien' })).not.toBeInTheDocument();
  });

  it('shows skeletons while loading', async () => {
    server.use(http.get('*/api/v1/dashboard', async () => { await delay('infinite'); return HttpResponse.json(dashboard()); }));
    await renderApp('/dashboard');
    expect(await screen.findByLabelText('Chargement du tableau de bord')).toHaveAttribute('aria-busy', 'true');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/dashboard');
    expect(await screen.findByRole('alert', {})).toBeInTheDocument();
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve(dashboard(), seen);
    await renderApp('/dashboard?scope=me');

    await tile('Patrimoine net');
    expect(seen).toEqual(['?scope=me']);
  });

  it('is the home page, first in the navigation', async () => {
    serve(dashboard());
    const { router } = await renderApp('/');

    await tile('Patrimoine net');
    expect(router.state.location.pathname).toBe('/dashboard');
    expect(screen.getAllByRole('link', { name: 'Accueil' }).length).toBeGreaterThan(0);
  });
});

describe('the dashboard hero', () => {
  it('the net worth is the hero figure', async () => {
    serve(dashboard());
    await renderApp('/dashboard');
    const worth = await tile('Patrimoine net');
    const figureEl = within(worth).getByText('12 900,00 €');
    expect(figureEl).toHaveClass('text-hero', 'tabular-nums');
    expect(within(worth).getByText('Patrimoine net')).toHaveClass('text-label');
  });

  it('an unknown net worth keeps its reason small', async () => {
    serve(dashboard({ netWorth: figure(null) }));
    await renderApp('/dashboard');
    const reason = within(await tile('Patrimoine net')).getByText('Inconnu : aucune valeur saisie');
    expect(reason).toHaveClass('text-sm');
    expect(reason).not.toHaveClass('text-hero');
  });

  it('the change is a line of text with its arrow, not a pill', async () => {
    serve(dashboard());
    await renderApp('/dashboard');
    const worth = await tile('Patrimoine net');
    const word = within(worth).getByText('en hausse');
    expect(word.closest('.rounded-full')).toBeNull();
    expect(worth.querySelector('.bg-status-good\\/10, .bg-status-critical\\/10, .rounded-full')).toBeNull();
    expect(worth).toHaveTextContent(/en hausse.*depuis le 1er janvier/);
  });

  it('the sparkline draws in the accent', async () => {
    server.use(http.get('*/api/v1/dashboard/net-worth', () => HttpResponse.json(netWorth([curvePoint({ on: '2025-08-31' }), curvePoint({ on: '2025-09-30', value: eur('1200.00') })]))));
    serve(dashboard());
    await renderApp('/dashboard');
    const worth = await tile('Patrimoine net');
    await waitFor(() => expect(worth.querySelector('svg[preserveAspectRatio]')).not.toBeNull());
    const svg = worth.querySelector('svg[preserveAspectRatio]')!;
    expect(svg.querySelector('path[stroke]')).toHaveAttribute('stroke', 'var(--primary)');
    svg.querySelectorAll('stop').forEach((stop) => expect(stop).toHaveAttribute('stop-color', 'var(--primary)'));
    expect(svg.innerHTML).not.toContain('--chart-1');
  });

  it('the key figure tiles are frosted material', async () => {
    serve(dashboard());
    await renderApp('/dashboard');
    const worth = await tile('Patrimoine net');
    for (const name of ['Patrimoine net', 'Revenus nets / mois', 'Reste à vivre', 'Engagements / mois']) {
      const el = await tile(name);
      expect(el).toHaveClass('material');
      expect(el).not.toHaveClass('bg-card');
    }
    expect(worth.className).not.toContain('from-primary/10');
  });
});
