import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Overview } from '@/api/subscriptions';
import { ALICE, BOB, eur, list, overview, persons, summary } from '../testData';
import { expectOnePrimary } from '@/test/headerActions';

const SPOTIFY = '0192f0aa-0000-7000-8000-0000000000d1';
const PRIME = '0192f0aa-0000-7000-8000-0000000000d2';
const CLAUDE_PRO = '0192f0aa-0000-7000-8000-0000000000d3';
const CLAUDE_API = '0192f0aa-0000-7000-8000-0000000000d4';
const ANTHROPIC = { key: 'anthropic', name: 'Anthropic', origin: 'catalog' as const, iconUrl: null, labels: [], included: [] };

const serve = (body: Overview) => server.use(http.get('*/api/v1/subscriptions/overview', () => HttpResponse.json(body)));
const region = (name: string) => screen.findByRole('region', { name });

describe('Abonnements › vue d’ensemble', () => {
  beforeAll(async () => { await import('../SubscriptionsPage'); await import('./OverviewTab'); }, 30_000);

  it('shows the server figures in a stats line', async () => {
    serve(overview({ activeCount: 3, endedCount: 1, monthlyAmortized: eur('20.00'), annualAmortized: eur('240.00'),
      monthCash: eur('30.00'), monthCashRemaining: eur('12.00') }));
    const { container } = await renderApp('/subscriptions');

    const figures = (await screen.findByText('Par mois')).closest('dl')!;
    expect(figures).toHaveTextContent('20,00 €');
    expect(figures).toHaveTextContent('240,00 €');
    expect(figures).toHaveTextContent('Reste à prélever12,00 €sur 30,00 €');
    expect(figures).toHaveTextContent('Abonnements3 actifs1 fini');
    await expectNoAxeViolations(container);
  });

  it('An_empty_household_sees_the_first_subscription_invite', async () => {
    const { container } = await renderApp('/subscriptions');

    const action = await screen.findByRole('link', { name: 'Ajouter votre premier abonnement' });
    expect(action).toHaveAttribute('href', '/subscriptions/new');
    expectOnePrimary(container);
    expect(screen.queryByText('Par mois')).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'À surveiller' })).not.toBeInTheDocument();
  });

  it('A_null_figure_shows_a_dash', async () => {
    serve(overview({ activeCount: 1, monthlyAmortized: null, monthCashRemaining: null }));
    await renderApp('/subscriptions');

    const figures = (await screen.findByText('Par mois')).closest('dl')!;
    expect(within(figures).getByText('Par mois').nextElementSibling).toHaveTextContent(/^—$/);
    expect(figures).toHaveTextContent('Reste à prélever—sur 0,00 €');
  });

  it('People_show_their_share_and_counts', async () => {
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)));
    serve(overview({ activeCount: 3, byPerson: [
      { personId: ALICE, amortized: eur('15.00'), ownedAlone: 2, shared: 1 },
      { personId: BOB, amortized: eur('0.00'), ownedAlone: 0, shared: 0 },
    ] }));
    await renderApp('/subscriptions?tab=analysis');

    const people = within(await region('Par personne')).getAllByRole('listitem');
    await waitFor(() => expect(people[0]).toHaveTextContent('Alice Dev'));
    expect(people[0]).toHaveTextContent('15,00 €');
    expect(people[0]).toHaveTextContent('2 seuls · 1 partagé');
    expect(people[1]).toHaveTextContent('Bob Dev');
    expect(people[1]).toHaveTextContent('0 seul · 0 partagé');
  });

  it('Labels_link_to_the_list_grouped_by_label', async () => {
    serve(overview({ activeCount: 2, byLabel: [
      { key: 'video', name: 'Vidéo', color: '#E50914', amortized: eur('20.00') },
      { key: 'none', name: null, color: null, amortized: eur('5.00') },
    ] }));
    const seen: string[] = [];
    server.use(http.get('*/api/v1/subscriptions', ({ request }) => {
      seen.push(new URL(request.url).search);
      return HttpResponse.json(list([summary({})]));
    }));
    const { user, router } = await renderApp('/subscriptions?tab=analysis');

    const labels = await region('Par libellé');
    const rows = within(labels).getAllByRole('listitem');
    expect(rows[0]).toHaveTextContent(/Vidéo.*20,00 €/);
    // One tone for the bars (dataviz §2): the label's colour is not painted.
    expect(rows[0]!.querySelector<HTMLElement>('.bg-primary')!.style.background).toBe('');
    expect(rows[1]).toHaveTextContent(/Sans libellé.*5,00 €/);
    const link = within(labels).getByRole('link', { name: 'Voir la liste' });
    expect(link).toHaveAttribute('href', '/subscriptions?tab=list&group=label&status=all');

    await user.click(link);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ tab: 'list', group: 'label', status: 'all' }));
    await waitFor(() => expect(new URLSearchParams(seen.at(-1)).get('group')).toBe('label'));
  });

  it('lists the upcoming charges, linked to their subscription, in the converted amount', async () => {
    serve(overview({ activeCount: 1, upcoming: [
      { date: '2026-10-28', itemId: SPOTIFY, displayName: 'Spotify', provider: null, price: { amount: '9.99', currency: 'USD' }, converted: eur('9.20'), isProjected: true },
      { date: '2026-11-02', itemId: PRIME, displayName: 'Prime', provider: null, price: { amount: '69.90', currency: 'USD' }, converted: null, isProjected: true },
    ] }));
    await renderApp('/subscriptions?tab=analysis');

    const [first, second] = within(await region('Prochains prélèvements')).getAllByRole('listitem');
    expect(first).toHaveTextContent(/28 oct\. 2026.*Spotify.*9,20 €/);
    expect(within(first!).getByRole('link', { name: 'Spotify' })).toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
    expect(second).toHaveTextContent(/69,90 \$US/);
  });

  it('Upcoming_links_to_the_calendar', async () => {
    serve(overview({ activeCount: 1 }));
    await renderApp('/subscriptions?scope=me&tab=analysis');

    const link = within(await region('Prochains prélèvements')).getByRole('link', { name: 'Calendrier' });
    expect(link).toHaveAttribute('href', '/subscriptions?scope=me&tab=calendar');
  });

  it('Clicking_a_month_opens_the_calendar_on_it', async () => {
    serve(overview({ activeCount: 1, months: [{ month: '2026-05', recurring: eur('17.20'), longCycle: eur('0.00') }] }));
    const { router, user } = await renderApp('/subscriptions');

    const link = await within(await region('12 prochains mois')).findByRole('link', { name: 'mai' });
    expect(link).toHaveAttribute('href', '/subscriptions?tab=calendar&month=2026-05');
    await user.click(link);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ tab: 'calendar', month: '2026-05' }));
  });

  it('shows the first eight upcoming charges and counts the others', async () => {
    const charge = (n: number) => ({ date: '2026-10-28', itemId: SPOTIFY, displayName: `Charge ${n}`, provider: null, price: eur('1.00'), converted: null, isProjected: true });
    serve(overview({ activeCount: 1, upcoming: Array.from({ length: 10 }, (_, n) => charge(n)) }));
    await renderApp('/subscriptions?tab=analysis');

    const rows = within(await region('Prochains prélèvements')).getAllByRole('listitem');
    expect(rows).toHaveLength(9);
    expect(rows[8]).toHaveTextContent('et 2 autres');
  });

  it('Each_watch_code_has_its_sentence_and_link', async () => {
    serve(overview({ activeCount: 4, watch: [
      { code: 'subscriptions.ends_without_renewal', itemIds: [SPOTIFY], date: '2026-12-31', amount: null },
      { code: 'subscriptions.long_cycle_charge_soon', itemIds: [PRIME], date: '2026-11-15', amount: eur('69.90') },
      { code: 'subscriptions.same_provider', itemIds: [CLAUDE_PRO, CLAUDE_API], date: null, amount: eur('40.00') },
    ] }));
    server.use(http.get('*/api/v1/subscriptions', () => HttpResponse.json(list([
      summary({ id: SPOTIFY, displayName: 'Spotify' }),
      summary({ id: PRIME, displayName: 'Amazon Prime' }),
      summary({ id: CLAUDE_PRO, displayName: 'Claude Pro', provider: ANTHROPIC }),
      summary({ id: CLAUDE_API, displayName: 'Claude API', provider: ANTHROPIC }),
    ]))));
    const { container } = await renderApp('/subscriptions');

    const watch = await region('À surveiller');
    // A grouped list, as on Placements and Biens: a link a row; an alert says so after its sentence.
    const ends = await within(watch).findByRole('link', { name: 'Spotify se termine le 31 décembre 2026 sans renouvellement Alerte' });
    expect(ends).toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
    expect(within(watch).getByRole('link', { name: /^Amazon Prime : 69,90\s€ prélevés le 15 novembre 2026$/ }))
      .toHaveAttribute('href', `/subscriptions/${PRIME}`);
    expect(within(watch).getByRole('link', { name: /^2 abonnements chez Anthropic : 40,00\s€ par mois$/ }))
      .toHaveAttribute('href', '/subscriptions?tab=list&group=provider');
    expect(within(watch).getAllByRole('listitem').map((li) => li.querySelector('[data-tone]')?.textContent ?? null))
      .toEqual(['Alerte', null, null]);
    expect(watch.querySelectorAll('li[data-tile]')).toHaveLength(3);
    await expectNoAxeViolations(container);
  });

  it('still says what to watch when the names cannot be read', async () => {
    serve(overview({ activeCount: 2, watch: [
      { code: 'subscriptions.ends_without_renewal', itemIds: [SPOTIFY], date: '2026-12-31', amount: null },
      { code: 'subscriptions.same_provider', itemIds: [CLAUDE_PRO, CLAUDE_API], date: null, amount: eur('40.00') },
    ] }));
    server.use(http.get('*/api/v1/subscriptions', () => HttpResponse.json({ status: 500 }, { status: 500 })));
    await renderApp('/subscriptions');

    const watch = await region('À surveiller');
    // A 5xx is retried twice with backoff before the error shows (createQueryClient).
    expect(await within(watch).findByRole('link', { name: 'Un abonnement se termine le 31 décembre 2026 sans renouvellement Alerte' }))
      .toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
    expect(within(watch).getByRole('link', { name: /^2 abonnements chez un même fournisseur : 40,00\s€ par mois$/ })).toBeInTheDocument();
  });

  it('keeps the simple view to the figures, the months and what to watch; « Analyse » has the rest (issue #20)', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/subscriptions/overview', ({ request }) => {
      seen.push(new URL(request.url).search);
      return HttpResponse.json(overview({ activeCount: 1 }));
    }));
    const { user, container } = await renderApp('/subscriptions');

    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['Vue d’ensemble', 'Analyse', 'Liste', 'Calendrier']);
    await region('À surveiller');
    expect(screen.getByRole('region', { name: '12 prochains mois' })).toBeInTheDocument();
    for (const name of ['Par personne', 'Par libellé', 'Prochains prélèvements'])
      expect(screen.queryByRole('region', { name })).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Analyse' }));
    for (const name of ['Par personne', 'Par libellé', 'Prochains prélèvements']) expect(await region(name)).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'À surveiller' })).not.toBeInTheDocument();
    expect(seen).toHaveLength(1);
    await expectNoAxeViolations(container);
  });

  it('shows three lines to watch, the others on demand', async () => {
    serve(overview({ activeCount: 4, watch: [
      { code: 'subscriptions.ends_without_renewal', itemIds: [SPOTIFY], date: '2026-12-31', amount: null },
      { code: 'subscriptions.long_cycle_charge_soon', itemIds: [PRIME], date: '2026-11-15', amount: eur('69.90') },
      { code: 'subscriptions.same_provider', itemIds: [CLAUDE_PRO, CLAUDE_API], date: null, amount: eur('40.00') },
      { code: 'subscriptions.long_cycle_charge_soon', itemIds: [CLAUDE_PRO], date: '2026-11-20', amount: eur('120.00') },
    ] }));
    const { user } = await renderApp('/subscriptions');

    const watch = await region('À surveiller');
    await waitFor(() => expect(within(watch).getAllByRole('listitem')).toHaveLength(3));
    await user.click(within(watch).getByRole('button', { name: 'Tout voir (4)' }));
    expect(within(watch).getAllByRole('listitem')).toHaveLength(4);
  });

  it('says there is nothing to watch', async () => {
    serve(overview({ activeCount: 1 }));
    await renderApp('/subscriptions');

    expect(await within(await region('À surveiller')).findByText('Rien à surveiller')).toBeInTheDocument();
  });

  it('Top_providers_open_the_list_on_their_provider', async () => {
    serve(overview({ activeCount: 1, endedCount: 1, topProviders: [{ provider: ANTHROPIC, spent: eur('480.00'), partial: true }] }));
    await renderApp('/subscriptions?tab=analysis');

    const row = within(await region('Fournisseurs qui ont le plus coûté')).getByRole('listitem');
    expect(row).toHaveTextContent(/Anthropic.*480,00 €.*partiel/);
    expect(within(row).getByRole('link', { name: 'Anthropic' }))
      .toHaveAttribute('href', '/subscriptions?tab=list&status=all&provider=anthropic');
  });

  it('Savings_show_the_twelve_months_total_and_the_list_behind_it', async () => {
    serve(overview({ activeCount: 1, endedCount: 1, savings: { last12Months: eur('60.00'), partial: false, items: [
      { itemId: PRIME, displayName: 'World of Warcraft', provider: null, endedOn: '2025-04-01', last12Months: eur('60.00') },
    ] } }));
    await renderApp('/subscriptions?tab=analysis');

    const savings = await region('Économisé grâce aux résiliations');
    expect(savings).toHaveTextContent('60,00 € depuis 12 mois');
    expect(within(savings).getByRole('listitem')).toHaveTextContent(/World of Warcraft.*Fini le 1 avril 2025.*60,00 €/);
  });

  it('Price_rises_list_increases_and_launch_offers_ending_and_are_absent_without_any', async () => {
    serve(overview({ activeCount: 2 }));
    await renderApp('/subscriptions?tab=analysis');
    await region('Par libellé');
    expect(screen.queryByRole('region', { name: 'Hausses de prix' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Économisé grâce aux résiliations' })).not.toBeInTheDocument();

    const month = { count: 1, unit: 'month' };
    serve(overview({ activeCount: 2, priceRises: { yearlyTotal: eur('36.00'), items: [
      { itemId: SPOTIFY, displayName: 'Spotify', provider: null, on: '2026-03-01', oldPrice: eur('9.99'), oldEvery: month,
        newPrice: eur('12.99'), newEvery: month, yearlyDifference: eur('36.00'), promoEnded: false, changePct: 10, real: { percent: 7.8, inflationTo: '2026-03', missing: null } },
      { itemId: PRIME, displayName: 'Prime', provider: null, on: '2026-02-01', oldPrice: eur('0.99'), oldEvery: month,
        newPrice: eur('9.99'), newEvery: month, yearlyDifference: eur('108.00'), promoEnded: true },
    ] } }));
    await renderApp('/subscriptions?tab=analysis');

    const rises = await region('Hausses de prix');
    expect(rises).toHaveTextContent('+36,00 € par an sur 12 mois');
    const [rise, promo] = within(rises).getAllByRole('listitem');
    expect(rise).toHaveTextContent(/Spotify.*9,99 € \/ 1 mois → 12,99 € \/ 1 mois le 1 mars 2026.*\+36,00 € par an/);
    expect(within(rise!).getByRole('link', { name: 'Spotify' })).toHaveAttribute('href', `/subscriptions/${SPOTIFY}?tab=history`);
    expect(rise).toHaveTextContent('+10,0 %, soit +7,8 % après inflation');
    expect(promo).toHaveTextContent('Fin d’offre de lancement');
    expect(promo).not.toHaveTextContent('108');
  });

  it('A_rise_without_a_real_figure_says_why_and_a_free_phase_shows_no_percent', async () => {
    const month = { count: 1, unit: 'month' };
    const rise = (id: string, name: string, extra: object) => ({ itemId: id, displayName: name, provider: null, on: '2026-03-01', oldPrice: eur('9.99'), oldEvery: month,
      newPrice: eur('12.99'), newEvery: month, yearlyDifference: eur('36.00'), promoEnded: false, ...extra });
    serve(overview({ activeCount: 3, priceRises: { yearlyTotal: eur('108.00'), items: [
      rise(SPOTIFY, 'Spotify', { changePct: 10, real: { percent: null, inflationTo: null, missing: 'currency' } }),
      rise(PRIME, 'Prime', { changePct: 10 }),
      rise('0192f0aa-0000-7000-8000-0000000000f3', 'Gratuit', { oldPrice: eur('0.00'), changePct: null, real: { percent: null, inflationTo: null, missing: null } }),
    ] } }));
    await renderApp('/subscriptions?tab=analysis');

    const [chf, absent, free] = within(await region('Hausses de prix')).getAllByRole('listitem');
    expect(chf).toHaveTextContent('+10,0 %');
    expect(chf).toHaveTextContent('Pas de chiffre après inflation hors euros');
    expect(absent).toHaveTextContent('Inflation observée indisponible');
    expect(free).toHaveTextContent('Gratuit');
    expect(free).not.toHaveTextContent('%');
  });

  it('The_months_card_header_switches_to_a_table', async () => {
    serve(overview({ activeCount: 1, months: [{ month: '2026-05', recurring: eur('17.20'), longCycle: eur('0.00') }] }));
    const { user } = await renderApp('/subscriptions');

    const months = await region('12 prochains mois');
    await user.click(await within(months).findByRole('button', { name: 'Afficher en tableau' }));
    expect(await within(months).findByTestId('months-table')).not.toHaveClass('sr-only');
    expect(within(months).getByRole('button', { name: 'Afficher le graphique' })).toBeInTheDocument();
  });

  it('Years_open_in_the_explorer', async () => {
    serve(overview({ activeCount: 1, years: [{ year: 2026, paid: eur('100.00'), upcoming: eur('20.00'), total: eur('120.00'), byLabel: [] }] }));
    const { user, router } = await renderApp('/subscriptions?tab=analysis');

    await user.click(await within(await region('Coût par an')).findByRole('link', { name: 'Voir la série' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/series'));
    expect(router.state.location.search).toMatchObject({ s: 'subscriptions:yearly' });
  });

  it('Years_switch_between_labels_and_total_and_show_a_table', async () => {
    const label = (paid: string) => [{ key: 'video', name: 'Vidéo', color: '#E50914', paid: eur(paid) }];
    serve(overview({ activeCount: 1, years: [
      { year: 2025, paid: eur('120.00'), upcoming: eur('0.00'), total: eur('120.00'), byLabel: label('120.00') },
      { year: 2026, paid: eur('100.00'), upcoming: eur('20.00'), total: eur('120.00'), byLabel: label('100.00') },
    ] }));
    const { user } = await renderApp('/subscriptions?tab=analysis');

    const years = await region('Coût par an');
    await user.click(await within(years).findByRole('button', { name: 'Afficher en tableau' }));
    const rows = within(years).getAllByRole('row');
    expect(rows[2]).toHaveTextContent(/2026.*100,00 €.*20,00 €.*120,00 €/);
    expect(within(years).queryByRole('columnheader', { name: 'Vidéo' })).not.toBeInTheDocument();
    await user.click(within(years).getByRole('radio', { name: 'Par libellé' }));
    expect(within(years).getByRole('columnheader', { name: 'Vidéo' })).toBeInTheDocument();
  });
});
