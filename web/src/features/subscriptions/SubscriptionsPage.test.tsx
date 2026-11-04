import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { expectLabelBefore } from '@/test/filters';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { ListResponse, Summary } from './api';
import { eur, list, overview, summary } from './testData';

const two = list([
  summary({}),
  summary({ id: '0192f0aa-0000-7000-8000-000000000002', displayName: 'Ancien VPN', nextChargeOn: null, nextChargeAmount: null,
    expiresOn: '2025-01-01', cashCost: [eur('7.49')], amortizedCost: [eur('7.49')], amortizedCostConverted: eur('7.49'),
    findings: [{ code: 'subscriptions.gap', message: 'Un trou de couverture existe.', reference: null }] }),
]);

/** The body rows of the table (the phone list below `md:` repeats them, so the table is the one queried). */
async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des abonnements' });
  return within(table).getAllByRole('row').slice(1);
}

const param = (seen: string[], name: string) => new URLSearchParams(seen.at(-1)).get(name);

const VIDEO = { key: 'streaming-video', name: 'Video', color: '#E50914' };
const VPN = { key: 'vpn', name: 'VPN', color: '#6D4AFF' };
const serveLabels = () => server.use(http.get('*/api/v1/subscriptions/labels', () => HttpResponse.json([VIDEO, VPN])));

function serve(body: ListResponse, seen: string[] = []) {
  server.use(http.get('*/api/v1/subscriptions', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
}

describe('Abonnements › liste', () => {
  beforeAll(async () => { await import('./SubscriptionsPage'); }, 30_000);
  afterEach(() => vi.useRealTimers());

  it('shows one table row per item with its owner, and the household totals in a stats line', async () => {
    const alice = '0192f0aa-0000-7000-8000-0000000000a1';
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: alice, kind: 'adult', displayName: 'Alice', email: null }])));
    serve(list([summary({ owners: [{ personId: alice, shareBp: 10000 }] }), two.items[1]!], { householdCashCostConverted: eur('18.50') }));
    const { container } = await renderApp('/subscriptions?tab=list');

    const table = await screen.findByRole('table', { name: 'Liste des abonnements' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Fournisseur', 'Propriétaire', 'Libellés', 'Prochaine échéance', 'Par mois', 'Par an']);
    const shown = await rows();
    expect(shown).toHaveLength(2);
    expect(within(shown[0]!).getByText('Netflix')).toBeInTheDocument();
    await waitFor(() => expect(shown[0]).toHaveTextContent('Alice'));
    expect(shown[0]).toHaveTextContent('13,49 €');
    expect(shown[0]).toHaveTextContent('161,88 €');
    expect(shown[0]).toHaveTextContent('1 nov. 2025');
    expect(shown[1]).toHaveTextContent('Expire le 1 janv. 2025');
    const totals = screen.getByText('251,76 €').closest('dl')!;
    expect(within(totals).getByText('Par mois')).toBeInTheDocument();
    expect(within(totals).getByText('Par an')).toBeInTheDocument();
    expect(within(totals).getByText('Décaissé du mois')).toBeInTheDocument();
    expect(within(totals).getByText('18,50 €')).toBeInTheDocument();
    expect(screen.queryByText(/compte une seule fois/)).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('the list filters sit in one row, search first', async () => {
    serveLabels();
    serve(two);
    await renderApp('/subscriptions?tab=list');
    await rows();

    const row = screen.getByRole('group', { name: 'Filtres' });
    const search = within(row).getByRole('searchbox', { name: 'Rechercher par nom' });
    const status = within(row).getByRole('radiogroup', { name: 'État' });
    const group = within(row).getByRole('radiogroup', { name: 'Grouper par' });
    const labels = await within(row).findByRole('button', { name: /^Libellés/ });
    expectLabelBefore(within(row).getByText('Rechercher par nom'), search);
    expectLabelBefore(within(row).getByText('État'), status);
    expectLabelBefore(within(row).getByText('Grouper par'), group);
    const order = [search, status, group, labels];
    order.slice(1).forEach((el, i) => expect(order[i]!.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy());
    const totals = screen.getByRole('region', { name: 'Total du foyer' });
    expect(row.compareDocumentPosition(totals) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('sorts by the cost of the month', async () => {
    serve(two);
    const { user } = await renderApp('/subscriptions?tab=list');

    const names = async () => (await rows()).map((r) => within(r).getAllByRole('cell')[0]!.textContent?.match(/Netflix|Ancien VPN/)?.[0]);
    expect(await names()).toEqual(['Netflix', 'Ancien VPN']);
    await user.click(within(screen.getByRole('table', { name: 'Liste des abonnements' })).getByRole('button', { name: 'Par mois' }));
    expect(await names()).toEqual(['Ancien VPN', 'Netflix']);
  });

  it('does not show the findings on the rows', async () => {
    serve(two);
    await renderApp('/subscriptions?tab=list');

    await rows();
    expect(screen.queryByText('Un trou de couverture existe.')).not.toBeInTheDocument();
    expect(screen.queryByRole('list', { name: 'Points à vérifier' })).not.toBeInTheDocument();
  });

  it('says why the totals are not the sum of the rows when purchases are shared', async () => {
    serve(list([summary({})], { sharedPurchaseIds: ['0192f0aa-0000-7000-8000-00000000000f'] }));
    await renderApp('/subscriptions?tab=list');

    expect(await screen.findByText(/« Décaissé du mois » compte une seule fois/)).toBeInTheDocument();
  });

  it('invites to add the first subscription when there is none', async () => {
    serve(list([], { householdCashCost: [], householdAmortizedCost: [] }));
    await renderApp('/subscriptions?tab=list');

    const action = await screen.findByRole('link', { name: 'Ajouter votre premier abonnement' });
    expect(action).toHaveAttribute('href', '/subscriptions/new');
  });

  it('writes the name and the status into the address and asks the server for them', async () => {
    const seen: string[] = [];
    serve(two, seen);
    const { user, router } = await renderApp('/subscriptions?tab=list');

    await rows();
    await user.type(screen.getByLabelText('Rechercher par nom'), 'vpn');
    // The filters keep the list open (overview plan, Review Focus 5).
    await waitFor(() => expect(router.state.location.search).toMatchObject({ tab: 'list', q: 'vpn' }));
    await waitFor(() => expect(seen.at(-1)).toContain('q=vpn'));

    await user.click(screen.getByRole('radio', { name: 'Finis' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ status: 'ended' }));
    await waitFor(() => expect(seen.at(-1)).toContain('status=ended'));
  });

  it('asks the server for the scope in the URL', async () => {
    const seen: string[] = [];
    serve(two, seen);
    await renderApp('/subscriptions?tab=list&scope=me');

    await rows();
    expect(seen.at(-1)).toContain('scope=me');
  });

  it('shows the problem when the list cannot be read', async () => {
    server.use(http.get('*/api/v1/subscriptions', () => HttpResponse.json({ status: 500 }, { status: 500 })));
    await renderApp('/subscriptions?tab=list');

    // A 5xx is retried twice with backoff before the error shows (createQueryClient).
    expect(await screen.findByRole('alert', {})).toHaveTextContent('Une erreur inattendue');
  });

  const labelled = list([
    summary({ displayName: 'Netflix', provider: { key: 'netflix', name: 'Netflix', origin: 'catalog', iconUrl: null, labels: [VIDEO], included: [] } }),
    summary({ id: '0192f0aa-0000-7000-8000-000000000002', displayName: 'Proton VPN', provider: { key: 'proton', name: 'Proton',
      origin: 'catalog', iconUrl: null, labels: [VPN], included: [] } }),
    summary({ id: '0192f0aa-0000-7000-8000-000000000003', displayName: 'Sans fournisseur' }),
  ]);

  const openLabels = async (user: Awaited<ReturnType<typeof renderApp>>['user']) => {
    await user.click(await screen.findByRole('button', { name: /^Libellés/ }));
    return screen.findByRole('menu');
  };

  it('offers every label in a multi-select menu, writes the ones on into the address and the request, and clears them', async () => {
    serveLabels();
    const seen: string[] = [];
    serve(labelled, seen);
    const { user, router, container } = await renderApp('/subscriptions?tab=list');
    const menu = await openLabels(user);
    expect(within(menu).getAllByRole('menuitemcheckbox').map((b) => b.textContent)).toEqual(['Video', 'VPN']);

    await user.click(within(menu).getByRole('menuitemcheckbox', { name: 'VPN' }));
    await waitFor(() => expect(within(menu).getByRole('menuitemcheckbox', { name: 'VPN' })).toBeChecked());
    await waitFor(() => expect(router.state.location.search).toMatchObject({ label: 'vpn' }));
    await waitFor(() => expect(param(seen, 'label')).toBe('vpn'));

    await user.click(within(menu).getByRole('menuitemcheckbox', { name: 'Video' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ label: 'vpn,streaming-video' }));
    await waitFor(() => expect(param(seen, 'label')).toBe('vpn,streaming-video'));
    await expectNoAxeViolations(container);

    await user.keyboard('{Escape}');
    await user.click(screen.getByRole('button', { name: 'Effacer les libellés' }));
    // The unfiltered answer is cached from the first request: the address alone says the labels are off.
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('label'));
    expect(screen.queryByRole('button', { name: 'Effacer les libellés' })).not.toBeInTheDocument();
  });

  it('reads the labels from the address and ignores an unknown or repeated one', async () => {
    serveLabels();
    const seen: string[] = [];
    serve(labelled, seen);
    const { user } = await renderApp('/subscriptions?tab=list&label=vpn,vpn,nope');
    const menu = await openLabels(user);
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'VPN' })).toBeChecked();
    expect(within(menu).queryByRole('menuitemcheckbox', { name: 'nope' })).not.toBeInTheDocument();
    await waitFor(() => expect(param(seen, 'label')).toBe('vpn'));
  });

  it('shows no label menu when no label exists', async () => {
    serve(two);
    await renderApp('/subscriptions?tab=list');
    await rows();
    expect(screen.queryByRole('button', { name: /^Libellés/ })).not.toBeInTheDocument();
  });
});

const ANTHROPIC = { key: 'anthropic', name: 'Anthropic', origin: 'catalog' as const, iconUrl: null, labels: [], included: [] };
const claude = summary({ id: '0192f0aa-0000-7000-8000-0000000000c1', displayName: 'Claude Pro', provider: ANTHROPIC });
const api = summary({ id: '0192f0aa-0000-7000-8000-0000000000c2', displayName: 'Claude API', provider: ANTHROPIC });
const netflix = summary({});
const group = (key: string, items: Summary[], over: Partial<ListResponse['groups'][number]> = {}) => ({
  key, name: key, itemIds: items.map((i) => i.id),
  items: items.map((i) => ({ id: i.id, amortizedCostConverted: i.amortizedCostConverted, annualCostConverted: i.annualCostConverted })),
  amortizedCostConverted: eur('40.00'), annualCostConverted: eur('480.00'),
  convertedIsEstimate: false, ...over,
});
const grouped = list([claude, api, netflix], { groups: [group('Anthropic', [claude, api]), group('Netflix', [netflix])] });

describe('Abonnements › onglets', () => {
  beforeAll(async () => { await import('./SubscriptionsPage'); }, 30_000);

  it('The_overview_is_the_default_tab', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/subscriptions/overview', ({ request }) => {
      seen.push(new URL(request.url).search);
      return HttpResponse.json(overview({ activeCount: 1, monthlyAmortized: eur('13.49') }));
    }));
    const { router } = await renderApp('/subscriptions?scope=me');

    expect(await screen.findByText('Par mois')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
    expect(param(seen, 'scope')).toBe('me');
    expect(router.state.location.search).not.toHaveProperty('tab');
  });

  it('The_list_tab_keeps_its_filters', async () => {
    const seen: string[] = [];
    serve(grouped, seen);
    await renderApp('/subscriptions?tab=list&group=label');

    await rows();
    expect(screen.getByRole('tab', { name: 'Liste' })).toHaveAttribute('aria-selected', 'true');
    expect(param(seen, 'group')).toBe('label');
  });
});

describe('Abonnements › liste groupée', () => {
  beforeAll(async () => { await import('./SubscriptionsPage'); }, 30_000);

  it('It_asks_the_server_for_active_subscriptions_grouped_by_provider_by_default', async () => {
    const seen: string[] = [];
    serve(grouped, seen);
    const { router } = await renderApp('/subscriptions?tab=list');

    await rows();
    expect(param(seen, 'status')).toBe('active');
    expect(param(seen, 'group')).toBe('provider');
    expect(router.state.location.search).not.toHaveProperty('status');
    expect(router.state.location.search).not.toHaveProperty('group');
  });

  it('A_provider_with_two_subscriptions_is_one_expandable_row', async () => {
    serve(grouped);
    const { user } = await renderApp('/subscriptions?tab=list');

    const shown = await rows();
    expect(shown).toHaveLength(2);
    expect(shown[0]).toHaveTextContent('Anthropic · 2 abonnements');
    expect(shown[0]).toHaveTextContent('40,00 €');
    expect(screen.queryByText('Claude Pro')).not.toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Liste des abonnements' });
    await user.click(within(table).getByRole('button', { name: 'Déplier Anthropic' }));
    expect(within(table).getByText('Claude Pro')).toBeInTheDocument();
    expect(within(table).getByText('Claude API')).toBeInTheDocument();
  });

  it('A_single_subscription_group_links_to_its_detail', async () => {
    serve(grouped);
    await renderApp('/subscriptions?tab=list');

    const shown = await rows();
    expect(within(shown[1]!).getByRole('link')).toHaveAttribute('href', `/subscriptions/${netflix.id}`);
    expect(within(shown[1]!).queryByRole('button')).not.toBeInTheDocument();
    expect(within(shown[0]!).queryByRole('link')).not.toBeInTheDocument();
  });

  it('Group_by_is_kept_in_the_address', async () => {
    const seen: string[] = [];
    serve(grouped, seen);
    const { user, router } = await renderApp('/subscriptions?tab=list');

    await rows();
    await user.click(screen.getByRole('radio', { name: 'Libellé' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ group: 'label' }));
    await waitFor(() => expect(param(seen, 'group')).toBe('label'));
  });

  it('names a person group from the persons, and a person with one subscription above it, linked to its detail', async () => {
    const alice = '0192f0aa-0000-7000-8000-0000000000a1';
    const bob = '0192f0aa-0000-7000-8000-0000000000b1';
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([
      { id: alice, kind: 'adult', displayName: 'Alice', email: null }, { id: bob, kind: 'adult', displayName: 'Bob', email: null }])));
    serve(list([claude, api, netflix], { groups: [
      group(alice, [claude, api], { name: null }), group(bob, [netflix], { name: null, amortizedCostConverted: eur('6.75'),
        items: [{ id: netflix.id, amortizedCostConverted: eur('6.75'), annualCostConverted: eur('81.00') }] })] }));
    await renderApp('/subscriptions?tab=list&group=person');

    const shown = await rows();
    await waitFor(() => expect(shown[0]).toHaveTextContent('Alice · 2 abonnements'));
    expect(shown[1]).toHaveTextContent('Bob · Netflix');
    expect(shown[1]).toHaveTextContent('6,75 €');
    expect(within(shown[1]!).getByRole('link')).toHaveAttribute('href', `/subscriptions/${netflix.id}`);
  });

  it('shows each share of a person on the subscriptions under a person group, under a Personne column', async () => {
    const alice = '0192f0aa-0000-7000-8000-0000000000a1';
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: alice, kind: 'adult', displayName: 'Alice', email: null }])));
    serve(list([claude, api], { groups: [group(alice, [claude, api], { name: null, amortizedCostConverted: eur('9.00'), items: [
      { id: claude.id, amortizedCostConverted: eur('6.745'), annualCostConverted: eur('80.94') },
      { id: api.id, amortizedCostConverted: eur('2.25'), annualCostConverted: eur('27.00') }] })] }));
    const { user } = await renderApp('/subscriptions?tab=list&group=person');

    const table = await screen.findByRole('table', { name: 'Liste des abonnements' });
    expect(within(table).getAllByRole('columnheader')[0]).toHaveTextContent('Personne');
    await user.click(await within(table).findByRole('button', { name: /^Déplier/ }));
    const [, first, second] = await rows();
    expect(first).toHaveTextContent(/Claude Pro.*6,7\d €.*80,94 €/);
    expect(second).toHaveTextContent(/Claude API.*2,25 €.*27,00 €/);
    expect(table).not.toHaveTextContent('13,49 €');
  });

  it('names the group without provider, and without label', async () => {
    serve(list([claude, api], { groups: [group('none', [claude, api], { name: null })] }));
    const { user } = await renderApp('/subscriptions?tab=list');

    expect((await rows())[0]).toHaveTextContent('Sans fournisseur · 2 abonnements');
    await user.click(screen.getByRole('radio', { name: 'Libellé' }));
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('Sans libellé · 2 abonnements'));
  });

  it('says no subscription matches when every subscription has ended, rather than inviting to add the first', async () => {
    server.use(http.get('*/api/v1/subscriptions', ({ request }) =>
      HttpResponse.json(new URL(request.url).searchParams.get('status') === 'all' ? grouped : list([]))));
    await renderApp('/subscriptions?tab=list');

    expect(await screen.findByText('Aucun abonnement ne correspond à ces filtres.')).toBeInTheDocument();
    expect(screen.queryByText('Aucun abonnement pour l\'instant')).not.toBeInTheDocument();
  });

  it('No_match_is_not_the_first_subscription_invite', async () => {
    serve(list([], { householdCashCost: [], householdAmortizedCost: [] }));
    await renderApp('/subscriptions?tab=list&q=zzz');

    expect(await screen.findByText('Aucun abonnement ne correspond à ces filtres.')).toBeInTheDocument();
    expect(screen.queryByText('Aucun abonnement pour l\'instant')).not.toBeInTheDocument();
  });

  it('Typing_keeps_the_previous_rows_while_loading', async () => {
    let release!: () => void;
    const later = new Promise<void>((resolve) => { release = resolve; });
    let asked = false;
    server.use(http.get('*/api/v1/subscriptions', async ({ request }) => {
      if (new URL(request.url).searchParams.get('q')) { asked = true; await later; }
      return HttpResponse.json(grouped);
    }));
    const { user } = await renderApp('/subscriptions?tab=list');

    expect(await rows()).toHaveLength(2);
    await user.type(screen.getByLabelText('Rechercher par nom'), 'c');
    // The new answer is pending: the page holds the previous rows instead of the loading skeleton.
    await waitFor(() => expect(asked).toBe(true));
    expect(screen.queryByRole('list', { name: 'Chargement des abonnements' })).not.toBeInTheDocument();
    expect(await rows()).toHaveLength(2);
    release();
  });

  it('A_null_group_amount_shows_a_dash', async () => {
    serve(list([claude, api], { groups: [group('Anthropic', [claude, api], { amortizedCostConverted: null, annualCostConverted: null })] }));
    await renderApp('/subscriptions?tab=list');

    const [row] = await rows();
    const cells = within(row!).getAllByRole('cell');
    expect(cells[4]).toHaveTextContent('—');
    expect(cells[5]).toHaveTextContent('—');
  });
});
