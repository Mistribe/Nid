import { screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { IncomeSourceList } from './api';
import { eur, list, summary } from './testData';

/** `withEnded` answers `archived=true`; the plain list answers everything else. */
function serve(body: IncomeSourceList, seen: string[] = [], withEnded: IncomeSourceList = body) {
  server.use(http.get('*/api/v1/income/sources', ({ request }) => {
    const search = new URL(request.url).search;
    seen.push(search);
    return HttpResponse.json(search.includes('archived=true') ? withEnded : body);
  }));
}

/** The body rows of the table (the phone list below `md:` repeats them, so the table is the one queried). */
async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des revenus' });
  return within(table).getAllByRole('row').slice(1);
}

describe('IncomeSourcesPage', () => {
  beforeAll(async () => { await import('./IncomeSourcesPage'); }, 30_000);

  it('shows one table row per item with its owner', async () => {
    const alice = '0192f0aa-0000-7000-8000-0000000000a1';
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: alice, kind: 'adult', displayName: 'Alice', email: null }])));
    serve(list([summary({ owners: [{ personId: alice, shareBp: 10000 }] })]));
    const { container } = await renderApp('/income');

    const table = await screen.findByRole('table', { name: 'Liste des revenus' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Nom', 'Type', 'Payeur', 'Propriétaire', 'Par mois']);
    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: /Salaire Alice/ })).toHaveAttribute('href', expect.stringContaining('/income/'));
    expect(row).toHaveTextContent('Salaire');
    expect(row).toHaveTextContent('Employeur');
    await waitFor(() => expect(row).toHaveTextContent('Alice'));
    expect(row).toHaveTextContent('2 450,00 €');
    expect(within(row).getByText('Privé')).toBeInTheDocument();
    expect(screen.getAllByText('Montants mensuels lissés sur l’année.')).toHaveLength(1);
    await expectNoAxeViolations(container);
  });

  it('sorts by the monthly equivalent, as the server rounded it', async () => {
    serve(list([summary({ displayName: 'Gros' }), summary({ id: 'b', displayName: 'Petit', monthlyEquivalent: eur('333.33') })]));
    const { user } = await renderApp('/income');

    const names = async () => (await rows()).map((r) => within(r).getAllByRole('cell')[0]!.textContent?.match(/^(Gros|Petit)/)?.[0]);
    expect(await names()).toEqual(['Gros', 'Petit']);
    expect((await rows())[1]).toHaveTextContent('333,33 €');
    await user.click(within(screen.getByRole('table', { name: 'Liste des revenus' })).getByRole('button', { name: 'Par mois' }));
    expect(await names()).toEqual(['Petit', 'Gros']);
  });

  it('warns of a component ending soon with nothing after it, in words', async () => {
    serve(list([summary({ gapSoon: ['Salaire de base'] })]));
    const { container } = await renderApp('/income');

    const row = (await rows())[0]!;
    expect(within(row).getByRole('note')).toHaveTextContent('« Salaire de base » se termine bientôt sans période suivante');
    await expectNoAxeViolations(container);
  });

  it('the add link is always shown, even when no row is writable', async () => {
    serve(list([summary({ canWrite: false })]));
    await renderApp('/income');

    await rows();
    expect(screen.getByRole('link', { name: 'Ajouter un revenu' })).toBeInTheDocument();
  });

  it('invites to add the first income when there is none at all', async () => {
    serve(list([]));
    const { container } = await renderApp('/income');

    expect(await screen.findByRole('heading', { name: 'Aucun revenu pour l’instant' })).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Ajouter un revenu' })[0]).toHaveAttribute('href', '/income/new');
    await expectNoAxeViolations(container);
  });

  it('says nothing is running when only ended sources exist, with a link that shows them', async () => {
    const seen: string[] = [];
    serve(list([]), seen, list([summary({ ended: true, endedOn: '2025-06-30' })]));
    const { user, router, container } = await renderApp('/income');

    expect(await screen.findByRole('heading', { name: 'Aucun revenu en cours' })).toBeInTheDocument();
    expect(seen).toEqual(['?scope=household', '?scope=household&archived=true']);
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('link', { name: 'Voir les sources terminées' }));

    await waitFor(() => expect(router.state.location.search).toMatchObject({ ended: 1 }));
    expect(screen.getByRole('switch', { name: 'Afficher les sources terminées' })).toBeChecked();
  });

  it('shows a skeleton while the list loads', async () => {
    server.use(http.get('*/api/v1/income/sources', async () => { await delay('infinite'); return HttpResponse.json(list([])); }));
    await renderApp('/income');

    expect(await screen.findByLabelText('Chargement des revenus')).toHaveAttribute('aria-busy', 'true');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/income/sources', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/income');

    expect(await screen.findByRole('alert', {}, { timeout: 8000 })).toBeInTheDocument();
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve(list([summary()]), seen);
    await renderApp('/income?scope=me');

    await rows();
    expect(seen).toEqual(['?scope=me']);
  });

  it('appears in the navigation, after Placements', async () => {
    serve(list([]));
    await renderApp('/income');
    expect(screen.getAllByRole('link', { name: 'Revenus' }).length).toBeGreaterThan(0);
  });
});

describe('IncomeSourcesPage, ended sources', () => {
  it('asks for the ended ones only when the box is checked, keeps that in the URL, and marks them in words', async () => {
    const seen: string[] = [];
    serve(list([summary()]), seen, list([summary(), summary({ id: 'old', displayName: 'Ancien emploi', ended: true, endedOn: '2024-12-31' })]));
    const { user, router, container } = await renderApp('/income');

    const box = await screen.findByRole('switch', { name: 'Afficher les sources terminées' });
    expect(box).not.toBeChecked();
    await rows();
    await user.click(box);

    await waitFor(() => expect(seen).toContain('?scope=household&archived=true'));
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
    serve(list([summary()]), seen);
    await renderApp('/income?ended=yes');

    await rows();
    expect(screen.getByRole('switch', { name: 'Afficher les sources terminées' })).not.toBeChecked();
    expect(seen).toEqual(['?scope=household']);
  });
});
