import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Overview } from '../api';
import { ALICE, BOB, eur, list, overview, persons, summary } from '../testData';

const SPOTIFY = '0192f0aa-0000-7000-8000-0000000000d1';
const PRIME = '0192f0aa-0000-7000-8000-0000000000d2';
const CLAUDE_PRO = '0192f0aa-0000-7000-8000-0000000000d3';
const CLAUDE_API = '0192f0aa-0000-7000-8000-0000000000d4';
const ANTHROPIC = { key: 'anthropic', name: 'Anthropic', origin: 'catalog' as const, iconUrl: null, labels: [] };

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
    expect(figures).toHaveTextContent('Reste à prélever12,00 € sur 30,00 €');
    expect(figures).toHaveTextContent('3 actifs · 1 fini');
    await expectNoAxeViolations(container);
  });

  it('An_empty_household_sees_the_first_subscription_invite', async () => {
    await renderApp('/subscriptions');

    const action = await screen.findByRole('link', { name: 'Ajouter votre premier abonnement' });
    expect(action).toHaveAttribute('href', '/subscriptions/new');
    expect(screen.queryByText('Par mois')).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'À surveiller' })).not.toBeInTheDocument();
  });

  it('A_null_figure_shows_a_dash', async () => {
    serve(overview({ activeCount: 1, monthlyAmortized: null, monthCashRemaining: null }));
    await renderApp('/subscriptions');

    const figures = (await screen.findByText('Par mois')).closest('dl')!;
    expect(within(figures).getByText('Par mois').nextElementSibling).toHaveTextContent(/^—$/);
    expect(figures).toHaveTextContent('Reste à prélever— sur 0,00 €');
  });

  it('People_show_their_share_and_counts', async () => {
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)));
    serve(overview({ activeCount: 3, byPerson: [
      { personId: ALICE, amortized: eur('15.00'), ownedAlone: 2, shared: 1 },
      { personId: BOB, amortized: eur('0.00'), ownedAlone: 0, shared: 0 },
    ] }));
    await renderApp('/subscriptions');

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
    const { user, router } = await renderApp('/subscriptions');

    const labels = await region('Par libellé');
    const rows = within(labels).getAllByRole('listitem');
    expect(rows[0]).toHaveTextContent(/Vidéo.*20,00 €/);
    expect(rows[1]).toHaveTextContent(/Sans libellé.*5,00 €/);
    const link = within(labels).getByRole('link', { name: 'Voir la liste' });
    expect(link).toHaveAttribute('href', '/subscriptions?tab=list&group=label&status=all');

    await user.click(link);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ tab: 'list', group: 'label', status: 'all' }));
    await waitFor(() => expect(new URLSearchParams(seen.at(-1)).get('group')).toBe('label'));
  });

  it('lists the upcoming charges, linked to their subscription, in the converted amount', async () => {
    serve(overview({ activeCount: 1, upcoming: [
      { date: '2026-10-28', itemId: SPOTIFY, displayName: 'Spotify', provider: null, price: { amount: '9.99', currency: 'USD' }, converted: eur('9.20') },
      { date: '2026-11-02', itemId: PRIME, displayName: 'Prime', provider: null, price: { amount: '69.90', currency: 'USD' }, converted: null },
    ] }));
    await renderApp('/subscriptions');

    const [first, second] = within(await region('Prochains prélèvements')).getAllByRole('listitem');
    expect(first).toHaveTextContent(/28 oct\. 2026.*Spotify.*9,20 €/);
    expect(within(first!).getByRole('link', { name: 'Spotify' })).toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
    expect(second).toHaveTextContent(/69,90 \$US/);
  });

  it('Upcoming_links_to_the_calendar', async () => {
    serve(overview({ activeCount: 1 }));
    await renderApp('/subscriptions?scope=me');

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
    const charge = (n: number) => ({ date: '2026-10-28', itemId: SPOTIFY, displayName: `Charge ${n}`, provider: null, price: eur('1.00'), converted: null });
    serve(overview({ activeCount: 1, upcoming: Array.from({ length: 10 }, (_, n) => charge(n)) }));
    await renderApp('/subscriptions');

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
    const ends = await within(watch).findByRole('link', { name: 'Spotify se termine le 31 décembre 2026 sans renouvellement' });
    expect(ends).toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
    expect(within(watch).getByRole('link', { name: /^Amazon Prime : 69,90\s€ prélevés le 15 novembre 2026$/ }))
      .toHaveAttribute('href', `/subscriptions/${PRIME}`);
    expect(within(watch).getByRole('link', { name: /^2 abonnements chez Anthropic : 40,00\s€ par mois$/ }))
      .toHaveAttribute('href', '/subscriptions?tab=list&group=provider');
    expect(within(watch).getAllByRole('listitem').map((li) => li.querySelector('[data-tone]')?.textContent))
      .toEqual(['Alerte', 'Info', 'Info']);
    await expectNoAxeViolations(container);
  });

  it('still says what to watch when the names cannot be read', { timeout: 10_000 }, async () => {
    serve(overview({ activeCount: 2, watch: [
      { code: 'subscriptions.ends_without_renewal', itemIds: [SPOTIFY], date: '2026-12-31', amount: null },
      { code: 'subscriptions.same_provider', itemIds: [CLAUDE_PRO, CLAUDE_API], date: null, amount: eur('40.00') },
    ] }));
    server.use(http.get('*/api/v1/subscriptions', () => HttpResponse.json({ status: 500 }, { status: 500 })));
    await renderApp('/subscriptions');

    const watch = await region('À surveiller');
    // A 5xx is retried twice with backoff before the error shows (createQueryClient).
    expect(await within(watch).findByRole('link', { name: 'Un abonnement se termine le 31 décembre 2026 sans renouvellement' }, { timeout: 8000 }))
      .toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
    expect(within(watch).getByRole('link', { name: /^2 abonnements chez un même fournisseur : 40,00\s€ par mois$/ })).toBeInTheDocument();
  });

  it('says there is nothing to watch', async () => {
    serve(overview({ activeCount: 1 }));
    await renderApp('/subscriptions');

    expect(await within(await region('À surveiller')).findByText('Rien à surveiller')).toBeInTheDocument();
  });
});
