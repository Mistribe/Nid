import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { InvestmentsOverview } from '../api';
import { ALICE, eur, household, LIVRET, overview, PEA } from '../testData';

const serve = (body: InvestmentsOverview, seen: string[] = []) =>
  server.use(http.get('*/api/v1/investments/overview', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
const region = (name: string) => screen.findByRole('region', { name });

describe('Placements › vue d’ensemble', () => {
  beforeAll(async () => { await import('../EnvelopesPage'); await import('./OverviewTab'); }, 30_000);

  it('opens on the overview and shows the server figures', async () => {
    const seen: string[] = [];
    serve(household(), seen);
    const { container } = await renderApp('/investments');

    expect(screen.getByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
    const figures = (await screen.findByText('Valeur des placements')).closest('dl')!;
    expect(figures).toHaveTextContent('35 000,00 €au 31 août 20251 valeur à mettre à jour');
    expect(figures).toHaveTextContent('Versé net31 800,00 €versements − retraits');
    expect(figures).toHaveTextContent('Gain+3 200,00 €+10,0 % · avant fiscalité');
    expect(figures).toHaveTextContent('Rendement annuel+6,2 % par an');
    expect(figures).toHaveTextContent('Depuis le 1er janvier+1 840,00 €hors versements · +5,6 %');
    expect(seen).toEqual(['?scope=household&span=12m']);
    await expectNoAxeViolations(container);
  });

  it('splits by kind and by person, and lists the envelopes with a total', async () => {
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: ALICE, kind: 'adult', displayName: 'Alice', email: null }])));
    serve(household());
    await renderApp('/investments');

    const kinds = await region('Répartition par type');
    const [pea0, livret0] = within(kinds).getAllByRole('row');
    expect(pea0).toHaveTextContent('PEA25 000,00 €71,4 %');
    expect(livret0).toHaveTextContent('Livret10 000,00 €28,6 %');
    expect(await region('Part de chacun')).toHaveTextContent('Alice35 000,00 €100,0 % · gain +3 200,00 €');

    const table = await screen.findByRole('table', { name: 'Enveloppes ouvertes' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Enveloppe', 'Propriétaires', 'Valeur', 'Gain', 'Rendement annuel', 'Dernière valeur']);
    const [pea, livret] = within(table).getAllByRole('row').slice(1);
    expect(within(pea!).getByRole('link', { name: /PEA/ })).toHaveAttribute('href', `/investments/${PEA}`);
    expect(pea).toHaveTextContent('+14,2 % par an');
    expect(livret).toHaveTextContent('31 août 2025');
    expect(await region('Enveloppes')).toHaveTextContent('Total · 2 enveloppes35 000,00 € · +3 200,00 €');
    expect(screen.getByRole('link', { name: 'Voir la liste' })).toHaveAttribute('href', expect.stringContaining('tab=list'));
  });

  it('says what to watch, each line to its envelope', async () => {
    serve(household());
    await renderApp('/investments');

    const watch = await region('À surveiller');
    const items = within(watch).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('Livret AValeur à mettre à jour, dernière au 31 août 2025');
    expect(within(items[0]!).getByRole('link')).toHaveAttribute('href', `/investments/${LIVRET}`);
    expect(items[1]).toHaveTextContent('PEAUn support arrive à échéance le 5 déc. 2025');
    expect(within(items[1]!).getByRole('link')).toHaveAttribute('href', `/investments/${PEA}?tab=holdings`);
  });

  it('keeps the span in the URL and shows the points as a table on demand', async () => {
    const seen: string[] = [];
    serve(household(), seen);
    const { user, router } = await renderApp('/investments');

    await user.click(await screen.findByRole('radio', { name: '3 ans' }));
    expect(router.state.location.search).toMatchObject({ curve: '3y' });
    expect(seen).toContain('?scope=household&span=3y');

    await user.click(screen.getByRole('button', { name: 'Afficher en tableau' }));
    const table = screen.getByRole('table', { name: 'Valeur et versements' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Date', 'PEA', 'Livret', 'Valeur', 'Versé net']);
    expect(within(table).getAllByRole('row').at(-1)).toHaveTextContent('Aujourd’hui25 000,00 €10 000,00 €35 000,00 €31 800,00 €');
  });

  it('says a figure a missing rate left out, and why', async () => {
    serve(overview({
      ...household(), value: null, gain: null,
      findings: [{ code: 'core.fx_rate_missing', message: 'No USD rate.', reference: 'USD:2025-10-15' }],
    }));
    await renderApp('/investments');

    const figures = (await screen.findByText('Valeur des placements')).closest('dl')!;
    expect(figures).toHaveTextContent('InconnuTaux de change manquant : USD');
  });

  it('without an envelope, offers to add one; with closed ones only, leads to them', async () => {
    serve(overview());
    await renderApp('/investments');
    expect(await screen.findByText('Aucune enveloppe pour l’instant')).toBeInTheDocument();

    serve(overview({ closedCount: 1 }));
    await renderApp('/investments');
    expect(await screen.findByRole('link', { name: 'Voir les enveloppes clôturées' }))
      .toHaveAttribute('href', expect.stringMatching(/tab=list.*closed=1|closed=1.*tab=list/));
  });

  it('an envelope in another currency shows its own amount, the converted one in a tooltip', async () => {
    const usd = { amount: '1200.0000', currency: 'USD' };
    const body = household();
    body.envelopes = [{ ...body.envelopes[0]!, currency: 'USD', figures: { ...body.envelopes[0]!.figures, value: usd }, valueConverted: eur('1080.00') }];
    serve(body);
    await renderApp('/investments');

    const table = await screen.findByRole('table', { name: 'Enveloppes ouvertes' });
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('1 200,00 $US');
  });
});
