import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { ListResponse, Summary } from './api';

const eur = (amount: string) => ({ amount, currency: 'EUR' });

const summary = (over: Partial<Summary>): Summary => ({
  id: '0192f0aa-0000-7000-8000-000000000001', displayName: 'Netflix', provider: null, autoRenew: true,
  expiresOn: null, nextChargeOn: '2025-11-01', nextChargeAmount: eur('13.49'),
  cashCost: [eur('13.49')], amortizedCost: [eur('13.49')], cashCostConverted: null, amortizedCostConverted: null,
  convertedIsEstimate: false, freeDays: 0, findings: [], ...over,
});

const list = (items: Summary[], over: Partial<ListResponse> = {}): ListResponse => ({
  items, householdCashCost: [eur('20.98')], householdAmortizedCost: [eur('20.98')],
  householdCashCostConverted: null, householdAmortizedCostConverted: null, householdConvertedIsEstimate: false,
  sharedPurchaseIds: [], findings: [], ...over,
});

const two = list([
  summary({}),
  summary({ id: '0192f0aa-0000-7000-8000-000000000002', displayName: 'Ancien VPN', nextChargeOn: null, nextChargeAmount: null,
    expiresOn: '2025-01-01', cashCost: [eur('7.49')], amortizedCost: [eur('7.49')],
    findings: [{ code: 'subscriptions.gap', message: 'Un trou de couverture existe.', reference: null }] }),
]);

/** The rows of the loaded list, not the skeleton's nor a row's findings. */
async function rows(): Promise<HTMLElement[]> {
  const list = await screen.findByRole('list', { name: 'Liste des abonnements' });
  return within(list).getAllByRole('listitem').filter((item) => item.parentElement === list);
}

function serve(body: ListResponse, seen: string[] = []) {
  server.use(http.get('*/api/v1/subscriptions', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
}

describe('Abonnements › liste', () => {
  afterEach(() => vi.useRealTimers());

  it('shows each subscription with its costs, next charge or expiry, findings and the household totals', async () => {
    serve(two);
    const { container } = await renderApp('/subscriptions');

    const shown = await rows();
    expect(shown).toHaveLength(2);
    expect(within(shown[0]!).getByText('Netflix')).toBeInTheDocument();
    expect(within(shown[0]!).getAllByText('13,49 €').length).toBeGreaterThan(0);
    expect(within(shown[0]!).getByText(/Prochaine échéance/)).toBeInTheDocument();
    expect(within(shown[1]!).getByText(/Expire le/)).toBeInTheDocument();
    expect(within(shown[1]!).getByText('Un trou de couverture existe.')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Total du foyer' })).toHaveTextContent('20,98 €');
    expect(screen.queryByText(/compte une seule fois/)).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says why the totals are not the sum of the rows when purchases are shared', async () => {
    serve(list([summary({})], { sharedPurchaseIds: ['0192f0aa-0000-7000-8000-00000000000f'] }));
    await renderApp('/subscriptions');

    expect(await screen.findByText(/compte une seule fois/)).toBeInTheDocument();
  });

  it('invites to add the first subscription when there is none', async () => {
    serve(list([], { householdCashCost: [], householdAmortizedCost: [] }));
    await renderApp('/subscriptions');

    const action = await screen.findByRole('link', { name: 'Ajouter votre premier abonnement' });
    expect(action).toHaveAttribute('href', '/subscriptions/new');
  });

  it('filters by name and status in the URL, in the browser', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2025-10-15T12:00:00Z'));
    serve(two);
    const { user, router } = await renderApp('/subscriptions');

    await rows();
    await user.type(screen.getByLabelText('Rechercher par nom'), 'vpn');
    await waitFor(() => expect(router.state.location.search).toMatchObject({ q: 'vpn' }));
    expect(await rows()).toHaveLength(1);

    await user.clear(screen.getByLabelText('Rechercher par nom'));
    await user.selectOptions(screen.getByLabelText('État'), 'ended');
    await waitFor(() => expect(router.state.location.search).toMatchObject({ status: 'ended' }));
    expect((await rows()).map((r) => within(r).getByRole('link').textContent)).toEqual([expect.stringContaining('Ancien VPN')]);
  });

  it('asks the server for the scope in the URL', async () => {
    const seen: string[] = [];
    serve(two, seen);
    await renderApp('/subscriptions?scope=me');

    await rows();
    expect(seen.at(-1)).toContain('scope=me');
  });

  it('shows the problem when the list cannot be read', { timeout: 10_000 }, async () => {
    server.use(http.get('*/api/v1/subscriptions', () => HttpResponse.json({ status: 500 }, { status: 500 })));
    await renderApp('/subscriptions');

    // A 5xx is retried twice with backoff before the error shows (createQueryClient).
    expect(await screen.findByRole('alert', {}, { timeout: 8000 })).toHaveTextContent('Une erreur inattendue');
  });

  const labelled = list([
    summary({ displayName: 'Netflix', provider: { key: 'netflix', name: 'Netflix', origin: 'catalog', iconUrl: null,
      labels: [{ key: 'streaming-video', name: 'Video', color: '#E50914' }] } }),
    summary({ id: '0192f0aa-0000-7000-8000-000000000002', displayName: 'Proton VPN', provider: { key: 'proton', name: 'Proton',
      origin: 'catalog', iconUrl: null, labels: [{ key: 'vpn', name: 'VPN', color: '#6D4AFF' }] } }),
    summary({ id: '0192f0aa-0000-7000-8000-000000000003', displayName: 'Sans fournisseur' }),
  ]);

  it('filters by the labels turned on, writes them into the address, and clears them', async () => {
    serve(labelled);
    const { user, router, container } = await renderApp('/subscriptions');
    const group = await screen.findByRole('group', { name: 'Libellés' });
    expect(within(group).getAllByRole('button').map((b) => b.textContent)).toEqual(['Video', 'VPN']);

    await user.click(within(group).getByRole('button', { name: 'VPN' }));
    expect(within(group).getByRole('button', { name: 'VPN' })).toHaveAttribute('aria-pressed', 'true');
    expect((await rows()).map((r) => within(r).getByRole('link').textContent)).toEqual([expect.stringContaining('Proton VPN')]);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ label: 'vpn' }));

    await user.click(within(group).getByRole('button', { name: 'Video' }));
    expect(await rows()).toHaveLength(2);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ label: 'vpn,streaming-video' }));
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Effacer les libellés' }));
    expect(await rows()).toHaveLength(3);
    expect(screen.queryByRole('button', { name: 'Effacer les libellés' })).not.toBeInTheDocument();
  });

  it('reads the labels from the address and ignores an unknown or repeated one', async () => {
    serve(labelled);
    await renderApp('/subscriptions?label=vpn,vpn,nope');
    const group = await screen.findByRole('group', { name: 'Libellés' });
    expect(within(group).getByRole('button', { name: 'VPN' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(group).queryByRole('button', { name: 'nope' })).not.toBeInTheDocument();
    expect(await rows()).toHaveLength(1);
  });

  it('shows no label group when no provider carries a label', async () => {
    serve(two);
    await renderApp('/subscriptions');
    await rows();
    expect(screen.queryByRole('group', { name: 'Libellés' })).not.toBeInTheDocument();
  });
});
