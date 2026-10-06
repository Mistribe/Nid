import { screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
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

async function rows(): Promise<HTMLElement[]> {
  const ul = await screen.findByRole('list', { name: 'Liste des revenus' });
  return within(ul).getAllByRole('listitem');
}

describe('IncomeSourcesPage', () => {
  it('shows each source with its kind, payer, monthly equivalent, components and access, smoothed over the year', async () => {
    serve(list([summary()]));
    const { container } = await renderApp('/income');

    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: 'Salaire Alice' })).toHaveAttribute('href', expect.stringContaining('/income/'));
    expect(row).toHaveTextContent('Salaire');
    expect(row).toHaveTextContent('Employeur');
    expect(row).toHaveTextContent('2 450,00 € par mois');
    expect(row).toHaveTextContent('2 composantes en cours');
    expect(within(row).getByText('Privé')).toBeInTheDocument();
    expect(screen.getAllByText('Montants mensuels lissés sur l’année.')).toHaveLength(1);
    await expectNoAxeViolations(container);
  });

  it('shows the monthly equivalent the server rounded, never computing it', async () => {
    serve(list([summary({ monthlyEquivalent: eur('333.33'), runningPhases: 1 })]));
    await renderApp('/income');

    const row = (await rows())[0]!;
    expect(row).toHaveTextContent('333,33 € par mois');
    expect(row).toHaveTextContent('1 composante en cours');
  });

  it('warns of a component ending soon with nothing after it, in words', async () => {
    serve(list([summary({ gapSoon: ['Salaire de base'] })]));
    const { container } = await renderApp('/income');

    const row = (await rows())[0]!;
    expect(within(row).getByRole('note')).toHaveTextContent('« Salaire de base » se termine bientôt sans période suivante');
    await expectNoAxeViolations(container);
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
    expect(screen.getByRole('checkbox', { name: 'Afficher les sources terminées' })).toBeChecked();
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

    const box = await screen.findByRole('checkbox', { name: 'Afficher les sources terminées' });
    expect(box).not.toBeChecked();
    await rows();
    await user.click(box);

    await waitFor(() => expect(seen).toContain('?scope=household&archived=true'));
    expect(router.state.location.search).toMatchObject({ ended: 1 });
    await waitFor(() => expect(within(screen.getByRole('list', { name: 'Liste des revenus' })).getAllByRole('listitem')).toHaveLength(2));
    const items = await rows();
    expect(within(items[1]!).getByText('Terminée')).toBeInTheDocument();
    expect(within(items[0]!).queryByText('Terminée')).not.toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Afficher les sources terminées' })).toHaveFocus();
    await expectNoAxeViolations(container);
  });

  it('starts checked from the URL and ignores any value other than 1', async () => {
    const seen: string[] = [];
    serve(list([summary()]), seen);
    await renderApp('/income?ended=yes');

    await rows();
    expect(screen.getByRole('checkbox', { name: 'Afficher les sources terminées' })).not.toBeChecked();
    expect(seen).toEqual(['?scope=household']);
  });
});
