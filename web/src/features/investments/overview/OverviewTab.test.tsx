import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
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
    expect(await region('Valeur des placements')).toHaveTextContent('35 000,00 €+1 840,00 €+5,6 %en haussedepuis le 1er janvier · hors versementsau 31 août 20251 valeur à mettre à jour');
    const figures = (await screen.findByText('Versé net', { selector: 'dt' })).closest('dl')!;
    expect(figures).toHaveTextContent('Versé net31 800,00 €versements − retraits');
    expect(figures).toHaveTextContent('Gain+3 200,00 €en hausse+10,0 % · avant fiscalité');
    expect(figures).toHaveTextContent('Rendement annuel+6,2 % par an');
    expect(seen).toEqual(['?scope=household&span=12m']);
    await expectNoAxeViolations(container);
  });

  it('leads with the hero: the value as the page’s first figure', async () => {
    serve(household());
    await renderApp('/investments');

    const hero = await region('Valeur des placements');
    expect(within(hero).getByText('35 000,00 €')).toHaveClass('text-hero', 'tabular-nums');
    // The curve is the server's points, a shape beside the figure.
    expect(hero.querySelector('svg[aria-hidden="true"] path[stroke]')).toHaveAttribute('stroke', 'var(--primary)');
    const figures = (await screen.findByText('Versé net', { selector: 'dt' })).closest('dl')!;
    expect(figures).not.toHaveTextContent('Valeur des placements');
    expect(figures).not.toHaveTextContent('35 000,00 €');
    expect(figures).not.toHaveTextContent('Depuis le 1er janvier');
  });

  it('shows the gain with its arrow, the figure uncoloured', async () => {
    serve(household());
    await renderApp('/investments');

    const gain = (await screen.findByText('Gain', { selector: 'dt' })).nextElementSibling!;
    expect(gain.className).not.toMatch(/text-status-good-text|text-destructive/);
    expect(gain.querySelector('svg')).toHaveClass('text-status-good');
    expect(within(gain as HTMLElement).getByText('en hausse')).toBeInTheDocument();
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
    expect(items[0]).toHaveTextContent('Livret A Valeur à mettre à jour, dernière au 31 août 2025');
    expect(within(items[0]!).getByRole('link')).toHaveAttribute('href', `/investments/${LIVRET}`);
    expect(items[1]).toHaveTextContent('PEA Un support arrive à échéance le 5 déc. 2025');
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
    // A point where an envelope counts at what was paid in says so; a valued one does not.
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('33 000,00 €dont versements pas encore valorisés');
    expect(within(table).getAllByRole('row').at(-1)).not.toHaveTextContent('pas encore valorisés');
  });

  it('says a figure a missing rate left out, and why', async () => {
    serve(overview({
      ...household(), value: null, gain: null,
      findings: [{ code: 'core.fx_rate_missing', message: 'No USD rate.', reference: 'USD:2025-10-15' }],
    }));
    await renderApp('/investments');

    const hero = await region('Valeur des placements');
    // The hero figure says « Inconnu » and why, never 0.
    expect(hero.querySelector('.text-hero')).toHaveTextContent(/^InconnuTaux de change manquant : USD$/);
    // Small and set back: a huge « Inconnu » would read as a data point.
    expect(within(hero.querySelector<HTMLElement>('.text-hero')!).getByText('Inconnu').closest('.text-muted-foreground'))
      .toHaveClass('text-sm', 'font-normal');
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

  it('says what is estimated: the count, the total and the row with its date', async () => {
    const body = household();
    body.estimatedCount = 1;
    body.envelopes = [{ ...body.envelopes[0]!, estimated: true, estimatedOn: '2025-10-12' }, body.envelopes[1]!];
    serve(body);
    const { container } = await renderApp('/investments');

    expect(await region('Valeur des placements')).toHaveTextContent('1 valeur estimée');
    const envelopes = await region('Enveloppes');
    expect(envelopes).toHaveTextContent('Total · 2 enveloppes35 000,00 € estimé · +3 200,00 €');
    const [pea, livret] = within(envelopes).getAllByRole('row').slice(1);
    expect(pea).toHaveTextContent('estimée au 12 oct. 2025');
    expect(livret).not.toHaveTextContent('estimée');
    const card = within(envelopes).getAllByRole('listitem').find((li) => li.textContent?.includes('PEA'));
    expect(card).toHaveTextContent('estimée au 12 oct. 2025');
    await expectNoAxeViolations(container);
  });

  it('says nothing about estimates when none is counted', async () => {
    serve(household());
    await renderApp('/investments');

    await region('Enveloppes');
    expect(document.body).not.toHaveTextContent(/estimée?\b/);
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
