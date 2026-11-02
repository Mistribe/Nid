import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { delay } from 'msw/utils/delay';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { IncomeOverview } from './api';
import { eur, overview, sourceRow } from './testData';

type Row = IncomeOverview['sources'][number];

/** `withEnded` answers `ended=true`; `body` every other request. A list without rows has no share either. */
function serve(body: Row[], seen: string[] = [], withEnded: Row[] = body) {
  server.use(http.get('*/api/v1/income/overview', ({ request }) => {
    const search = new URL(request.url).search;
    seen.push(search);
    const sources = search.includes('ended=true') ? withEnded : body;
    return HttpResponse.json(overview({ sources, ...(sources.length === 0 ? { split: [] } : {}) }));
  }));
}

/** The body rows of the table (the phone list below `md:` repeats them, so the table is the one queried). */
async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des revenus' });
  return within(table).getAllByRole('row').slice(1);
}

describe('IncomeSourcesPage', () => {
  beforeAll(async () => { await import('./IncomeSourcesPage'); }, 30_000);

  it('shows one table row per source with its owner, change, next payment and gap', async () => {
    const alice = '0192f0aa-0000-7000-8000-0000000000a1';
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: alice, kind: 'adult', displayName: 'Alice', email: null }])));
    serve([sourceRow({ owners: [{ personId: alice, shareBp: 10000 }], gapOn: '2026-12-31' })]);
    const { container } = await renderApp('/income');

    const table = await screen.findByRole('table', { name: 'Liste des revenus' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Nom', 'Propriétaires', 'Par mois', 'Sur 1 an', 'Prochain versement', 'Fin de période']);
    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: /Salaire Alice/ })).toHaveAttribute('href', expect.stringContaining('/income/'));
    await waitFor(() => expect(row).toHaveTextContent('Alice'));
    expect(row).toHaveTextContent('2 450,00 €');
    expect(row).toHaveTextContent('+4,1 %');
    expect(row).toHaveTextContent('2 300,00 €');
    expect(within(row).getByRole('note')).toHaveTextContent('sans suite');
    expect(within(row).getByText('Privé')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('sorts by the monthly equivalent, biggest first', async () => {
    serve([sourceRow({ id: 'b', name: 'Petit', perMonth: eur('333.33') }), sourceRow({ name: 'Gros' })]);
    const { user } = await renderApp('/income');

    const names = async () => (await rows()).map((r) => within(r).getAllByRole('cell')[0]!.textContent?.match(/^(Gros|Petit)/)?.[0]);
    expect(await names()).toEqual(['Gros', 'Petit']);
    await user.click(within(screen.getByRole('table', { name: 'Liste des revenus' })).getByRole('button', { name: 'Par mois' }));
    expect(await names()).toEqual(['Petit', 'Gros']);
  });

  it('a rent row links to its property', async () => {
    serve([sourceRow({ id: 'p1', module: 'realestate', name: 'Appartement Lyon' })]);
    await renderApp('/income');

    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: /Appartement Lyon/ })).toHaveAttribute('href', '/properties/p1?tab=rental');
  });

  it('the add link is always shown', async () => {
    serve([sourceRow()]);
    await renderApp('/income');

    await rows();
    expect(screen.getByRole('link', { name: 'Ajouter un revenu' })).toBeInTheDocument();
  });

  it('invites to add the first income when there is none at all', async () => {
    serve([]);
    const { container } = await renderApp('/income');

    expect(await screen.findByRole('heading', { name: 'Aucun revenu pour l’instant' })).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Ajouter un revenu' })[0]).toHaveAttribute('href', '/income/new');
    await expectNoAxeViolations(container);
  });

  it('shows no figures or chart above the invitation of a brand-new household', async () => {
    serve([]);
    await renderApp('/income');

    await screen.findByRole('heading', { name: 'Aucun revenu pour l’instant' });
    expect(screen.queryByText('Revenu / mois')).toBeNull();
    expect(screen.queryByRole('region', { name: /Mois par mois/ })).toBeNull();
  });

  it('says the filter is empty, not the household, and offers to clear it', async () => {
    serve([]);
    const { user, router } = await renderApp('/income?kind=rent');

    expect(await screen.findByRole('heading', { name: 'Aucun revenu pour ce filtre' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Aucun revenu pour l’instant' })).toBeNull();
    await user.click(screen.getByRole('link', { name: 'Retirer les filtres' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('kind'));
  });

  it('says nothing is running when only ended sources exist, with a link that shows them', async () => {
    const seen: string[] = [];
    serve([], seen, [sourceRow({ ended: true })]);
    const { user, router, container } = await renderApp('/income');

    expect(await screen.findByRole('heading', { name: 'Aucun revenu en cours' })).toBeInTheDocument();
    expect(seen.some((s) => s.includes('ended=true'))).toBe(true);
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('link', { name: 'Voir les sources terminées' }));

    await waitFor(() => expect(router.state.location.search).toMatchObject({ ended: 1 }));
    expect(screen.getByRole('switch', { name: 'Afficher les sources terminées' })).toBeChecked();
  });

  it('shows a skeleton while the overview loads', async () => {
    server.use(http.get('*/api/v1/income/overview', async () => { await delay('infinite'); return HttpResponse.json(overview()); }));
    await renderApp('/income');

    expect(await screen.findByLabelText('Chargement des revenus')).toHaveAttribute('aria-busy', 'true');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/income/overview', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/income');

    expect((await screen.findAllByRole('alert', {})).length).toBeGreaterThan(0);
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve([sourceRow()], seen);
    await renderApp('/income?scope=me');

    await rows();
    expect(seen[0]).toContain('scope=me');
  });

  it('appears in the navigation, after Placements', async () => {
    serve([]);
    await renderApp('/income');
    expect(screen.getAllByRole('link', { name: 'Revenus' }).length).toBeGreaterThan(0);
  });
});

describe('IncomeSourcesPage, ended sources', () => {
  it('shows ended sources on demand: asks ended=true, keeps it in the URL, marks them in words', async () => {
    const seen: string[] = [];
    serve([sourceRow()], seen, [sourceRow(), sourceRow({ id: 'old', name: 'Ancien emploi', ended: true })]);
    const { user, router, container } = await renderApp('/income');

    const box = await screen.findByRole('switch', { name: 'Afficher les sources terminées' });
    expect(box).not.toBeChecked();
    await rows();
    expect(seen.some((s) => s.includes('ended=true'))).toBe(false);
    await user.click(box);

    await waitFor(() => expect(seen.some((s) => s.includes('ended=true'))).toBe(true));
    expect(router.state.location.search).toMatchObject({ ended: 1 });
    await waitFor(async () => expect(await rows()).toHaveLength(2));
    const items = await rows();
    expect(within(items[1]!).getByText('Terminée')).toBeInTheDocument();
    expect(within(items[0]!).queryByText('Terminée')).not.toBeInTheDocument();
    expect(screen.getByRole('switch', { name: 'Afficher les sources terminées' })).toHaveFocus();
    await expectNoAxeViolations(container);
  });

  it('starts checked from the URL and ignores any value other than 1', async () => {
    const seen: string[] = [];
    serve([sourceRow()], seen);
    await renderApp('/income?ended=yes');

    await rows();
    expect(screen.getByRole('switch', { name: 'Afficher les sources terminées' })).not.toBeChecked();
    expect(seen.some((s) => s.includes('ended=true'))).toBe(false);
  });
});
