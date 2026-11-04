import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { EnvelopeList } from './api';
import { eur, figures, household, list, summary, unknownFigures } from './testData';

function serve(body: EnvelopeList, seen: string[] = []) {
  server.use(http.get('*/api/v1/investments/envelopes', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
}

/** The body rows of the table (the phone list below `md:` repeats them, so the table is the one queried). */
async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des enveloppes' });
  return within(table).getAllByRole('row').slice(1);
}

describe('EnvelopesPage', () => {
  beforeAll(async () => { await import('./EnvelopesPage'); }, 30_000);

  it('shows one table row per item with its owner', async () => {
    const alice = '0192f0aa-0000-7000-8000-0000000000a1';
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: alice, kind: 'adult', displayName: 'Alice', email: null }])));
    serve(list([summary({ owners: [{ personId: alice, shareBp: 10000 }] }), summary({ id: 'b', displayName: 'Livret A', kind: 'savings' })]));
    const { container } = await renderApp('/investments?tab=list');

    const table = await screen.findByRole('table', { name: 'Liste des enveloppes' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Nom', 'Type', 'Propriétaire', 'Valeur', 'Gain', 'Mise à jour']);
    const items = await rows();
    expect(items).toHaveLength(2);
    await waitFor(() => expect(items[0]).toHaveTextContent('Alice'));
    expect(items[0]).toHaveTextContent('PEA');
    expect(items[1]).toHaveTextContent('Livret A');
    expect(items[1]).toHaveTextContent('Livret');
    await expectNoAxeViolations(container);
  });

  it('sorts by value', async () => {
    serve(list([
      summary({ id: 'a', displayName: 'Gros', figures: figures({ value: eur('25000.0000') }) }),
      summary({ id: 'b', displayName: 'Petit', figures: figures({ value: eur('500.0000') }) }),
      summary({ id: 'c', displayName: 'Inconnu', figures: unknownFigures() }),
    ]));
    const { user } = await renderApp('/investments?tab=list');

    const names = async () => (await rows()).map((r) => within(r).getAllByRole('cell')[0]!.textContent);
    expect(await names()).toEqual(['Gros', 'Petit', 'Inconnu']);
    const table = screen.getByRole('table', { name: 'Liste des enveloppes' });
    await user.click(within(table).getByRole('button', { name: 'Valeur' }));
    expect(await names()).toEqual(['Petit', 'Gros', 'Inconnu']);
    await user.click(within(table).getByRole('button', { name: 'Valeur' }));
    expect(await names()).toEqual(['Gros', 'Petit', 'Inconnu']);
  });

  it('stale rows show their date with an icon', async () => {
    serve(list([summary({ figures: figures({ staleDays: 62 }) }), summary({ id: 'b', displayName: 'Frais' })]));
    await renderApp('/investments?tab=list');

    const [stale, fresh] = await rows();
    const cell = within(stale!).getAllByRole('cell')[5]!;
    expect(cell.querySelector('svg')).not.toBeNull();
    expect(cell).toHaveTextContent('30 sept. 2025');
    expect(within(cell).getByText('Valeur de plus de 62 jours')).toBeInTheDocument();
    expect(within(fresh!).getAllByRole('cell')[5]!.querySelector('svg')).toBeNull();
  });

  it('the add link is always shown, even when no row is writable', async () => {
    serve(list([summary({ canWrite: false })]));
    await renderApp('/investments?tab=list');

    await rows();
    expect(screen.getByRole('link', { name: 'Ajouter une enveloppe' })).toBeInTheDocument();
  });

  it('offers « Ajouter une enveloppe » to a writer', async () => {
    serve(list([summary(), summary({ id: 'r', canWrite: false })]));
    await renderApp('/investments?tab=list');

    await rows();
    expect(screen.getByRole('link', { name: 'Ajouter une enveloppe' })).toHaveAttribute('href', '/investments/new');
  });

  it('shows each envelope with its value, date and gain, before tax', async () => {
    serve(list([summary()]));
    const { container } = await renderApp('/investments?tab=list');

    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: 'PEA' })).toHaveAttribute('href', expect.stringContaining('/investments/'));
    expect(row).toHaveTextContent('PEA');
    expect(row).toHaveTextContent('25 000,00 €');
    expect(row).toHaveTextContent('30 sept. 2025');
    expect(row).toHaveTextContent('+3 000,00 €');
    expect(row).toHaveTextContent('+13,6 %');
    expect(within(row).getByText('en hausse')).toBeInTheDocument();
    expect(screen.getByText('Gains avant fiscalité.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says a loss in words, not only in colour', async () => {
    serve(list([summary({ figures: figures({ gain: eur('-500.0000'), gainPct: -2.3 }) })]));
    await renderApp('/investments?tab=list');

    const row = (await rows())[0]!;
    expect(row).toHaveTextContent('-500,00 €');
    expect(within(row).getByText('en baisse')).toBeInTheDocument();
  });

  it('never shows an unknown value as zero, and says it only once (not again for the gain)', async () => {
    serve(list([summary({ figures: unknownFigures() })]));
    await renderApp('/investments?tab=list');

    const row = (await rows())[0]!;
    expect(within(row).getAllByText('Aucune valeur pour l’instant')).toHaveLength(1);
    expect(row).not.toHaveTextContent('0,00 €');
  });

  it('shows an undefined percent as a dash, with the explanation for a screen reader alone', async () => {
    serve(list([summary({ figures: figures({ netContributions: eur('0.0000'), gain: eur('1000.0000'), gainPct: null, missingOpening: true }) })]));
    await renderApp('/investments?tab=list');

    const row = (await rows())[0]!;
    expect(row).toHaveTextContent('—');
    expect(within(row).getByText('Pourcentage non calculable : aucun versement enregistré')).toBeInTheDocument();
  });

  it('flags a stale value with its age', async () => {
    serve(list([summary({ figures: figures({ staleDays: 62 }) })]));
    await renderApp('/investments?tab=list');

    const row = (await rows())[0]!;
    expect(within(row).getByText('Valeur de plus de 62 jours')).toBeInTheDocument();
  });

  it('invites to add the first envelope', async () => {
    serve(list([]));
    const { container } = await renderApp('/investments?tab=list');

    expect(await screen.findByRole('heading', { name: 'Aucune enveloppe pour l’instant' })).toBeInTheDocument();
    for (const link of screen.getAllByRole('link', { name: 'Ajouter une enveloppe' })) expect(link).toHaveAttribute('href', '/investments/new');
    await expectNoAxeViolations(container);
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve(list([]), seen);
    await renderApp('/investments?tab=list&scope=me');

    await screen.findByRole('heading', { name: 'Aucune enveloppe pour l’instant' });
    expect(seen).toContain('?scope=me');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/investments/envelopes', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/investments?tab=list');

    expect(await screen.findByRole('alert', {})).toBeInTheDocument();
  });

  it('appears in the navigation only when the module is enabled', async () => {
    serve(list([]));
    await renderApp('/investments?tab=list');
    expect(screen.getAllByRole('link', { name: 'Placements' }).length).toBeGreaterThan(0);
  });
});

describe('EnvelopesPage, closed envelopes', () => {
  it('asks for the closed ones only when the box is checked, and keeps that in the URL', async () => {
    const seen: string[] = [];
    serve(list([summary()]), seen);
    const { user, router, container } = await renderApp('/investments?tab=list');

    const box = await screen.findByRole('switch', { name: 'Afficher les clôturées' });
    expect(box).not.toBeChecked();
    expect(seen).toEqual(['?scope=household']);
    await user.click(box);

    await waitFor(() => expect(seen).toContain('?scope=household&archived=true'));
    expect(router.state.location.search).toMatchObject({ closed: 1 });
    await rows();
    expect(screen.getByRole('switch', { name: 'Afficher les clôturées' })).toBeChecked();
    expect(screen.getByRole('switch', { name: 'Afficher les clôturées' })).toHaveFocus();
    await user.click(screen.getByRole('switch', { name: 'Afficher les clôturées' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('closed'));
    await expectNoAxeViolations(container);
  });

  it('marks a closed row in words, and starts checked from the URL', async () => {
    const seen: string[] = [];
    serve(list([summary(), summary({ id: 'old', displayName: 'Ancien', closed: true })]), seen);
    const { container } = await renderApp('/investments?tab=list&closed=1');

    const items = await rows();
    expect(screen.getByRole('switch', { name: 'Afficher les clôturées' })).toBeChecked();
    expect(seen).toContain('?scope=household&archived=true');
    expect(within(items[1]!).getByText('Clôturée')).toBeInTheDocument();
    expect(within(items[0]!).queryByText('Clôturée')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('keeps the box when no envelope is left to show', async () => {
    serve(list([]));
    await renderApp('/investments?tab=list&closed=1');

    expect(await screen.findByRole('heading', { name: 'Aucune enveloppe pour l’instant' })).toBeInTheDocument();
    expect(screen.getByRole('switch', { name: 'Afficher les clôturées' })).toBeChecked();
  });

  it('ignores a closed value other than 1: unchecked, no archived', async () => {
    const seen: string[] = [];
    serve(list([summary()]), seen);
    await renderApp('/investments?tab=list&closed=yes');

    await rows();
    expect(screen.getByRole('switch', { name: 'Afficher les clôturées' })).not.toBeChecked();
    expect(seen).toEqual(['?scope=household']);
  });

  it('tells two envelopes of the same name apart by their owner, and shows the shares of a split one', async () => {
    const alice = '0192f0aa-0000-7000-8000-0000000000a1';
    const bob = '0192f0aa-0000-7000-8000-0000000000b2';
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([
      { id: alice, kind: 'adult', displayName: 'Alice', email: null },
      { id: bob, kind: 'adult', displayName: 'Bob', email: null },
    ])));
    serve(list([
      summary({ id: 'a', displayName: 'Livret A', owners: [{ personId: alice, shareBp: 10000 }] }),
      summary({ id: 'b', displayName: 'Livret A', owners: [{ personId: bob, shareBp: 10000 }] }),
      summary({ id: 'c', displayName: 'Compte joint', owners: [{ personId: alice, shareBp: 6000 }, { personId: bob, shareBp: 4000 }] }),
    ]));
    const { container } = await renderApp('/investments?tab=list');

    const items = await rows();
    await waitFor(() => expect(items[0]).toHaveTextContent('Alice'));
    expect(items[0]).not.toHaveTextContent('Bob');
    expect(items[1]).toHaveTextContent('Bob');
    expect(items[1]).not.toHaveTextContent('Alice');
    expect(items[2]).toHaveTextContent('Alice');
    expect(items[2]).toHaveTextContent('Bob');
    expect(items[2]).toHaveTextContent('60 % / 40 %');
    await expectNoAxeViolations(container);
  });

  it('the overview outline has one h1 and level-2 sections', async () => {
    server.use(http.get('*/api/v1/investments/overview', () => HttpResponse.json(household())));
    await renderApp('/investments');

    await screen.findByRole('heading', { level: 2, name: 'Répartition par type' });
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
    expect(screen.getAllByRole('heading', { level: 2 }).length).toBeGreaterThan(0);
  });
});
