import { screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PropertyList } from './api';
import { PROPERTY, propertyList, propertySummary } from './testData';

function serve(body: PropertyList, seen: string[] = []) {
  server.use(http.get('*/api/v1/realestate/properties', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
}

/** The body rows of the table (the phone list below `md:` repeats them, so the table is the one queried). */
async function rows(): Promise<HTMLElement[]> {
  return within(await screen.findByRole('table', { name: 'Liste des biens' })).getAllByRole('row').slice(1);
}

const parking = propertySummary({
  id: '0192f0bb-0000-7000-8000-00000000e002', name: 'Parking gare', type: 'parking', visibility: 'private', value: null,
  valueOn: null, netValue: null,
});

describe('PropertiesPage', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('./PropertiesPage'); }, 30_000);

  it('shows one table row per item with its owner', async () => {
    const alice = '0192f0aa-0000-7000-8000-0000000000a1';
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: alice, kind: 'adult', displayName: 'Alice', email: null }])));
    const seen: string[] = [];
    serve(propertyList([propertySummary({ owners: [{ personId: alice, shareBp: 10000 }] })]), seen);
    const { container } = await renderApp('/properties?tab=list');

    const table = await screen.findByRole('table', { name: 'Liste des biens' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Nom', 'Type', 'Propriétaire', 'Valeur', 'Valeur nette', 'Rendement brut']);
    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: /Maison de Nantes/ })).toHaveAttribute('href', `/properties/${PROPERTY}`);
    expect(row).toHaveTextContent('Résidence principale');
    await waitFor(() => expect(row).toHaveTextContent('Alice'));
    expect(row).toHaveTextContent('290 000,00 €');
    expect(row).toHaveTextContent('1 juin 2026');
    expect(row).toHaveTextContent('96 435,77 €');
    expect(row).not.toHaveTextContent('À réestimer');
    expect(seen).toEqual(['?scope=household']);
    await expectNoAxeViolations(container);
  });

  it('sorts by value', async () => {
    serve(propertyList([propertySummary(), parking, propertySummary({ id: 'x', name: 'Studio', value: '90000.00' })]));
    const { user } = await renderApp('/properties?tab=list');

    const names = async () => (await rows()).map((r) => within(r).getAllByRole('cell')[0]!.textContent);
    expect(await names()).toEqual(['Maison de NantesPartagé', 'Parking garePrivé', 'StudioPartagé']);
    await user.click(within(screen.getByRole('table', { name: 'Liste des biens' })).getByRole('button', { name: 'Valeur' }));
    expect(await names()).toEqual(['StudioPartagé', 'Maison de NantesPartagé', 'Parking garePrivé']);
  });

  it('stale rows show their date with an icon and « À réestimer » in words; a property never estimated says so', async () => {
    serve(propertyList([propertySummary({ stale: true }), parking]));
    const { container } = await renderApp('/properties?tab=list');

    const [stale, never] = await rows();
    const cell = within(stale!).getAllByRole('cell')[3]!;
    expect(cell.querySelector('svg')).not.toBeNull();
    expect(cell).toHaveTextContent('À réestimer');
    expect(cell).toHaveTextContent('1 juin 2026');
    expect(never).toHaveTextContent('Pas encore estimé');
    await expectNoAxeViolations(container);
  });

  it('shows the gross yield in %, « — » when the server gives none, and the access of a private property', async () => {
    serve(propertyList([propertySummary({ grossYield: '0.0384' }), parking]));
    const { container } = await renderApp('/properties?tab=list');

    const [rented, unknown] = await rows();
    expect(within(rented!).getAllByRole('cell')[5]).toHaveTextContent('3,84 %');
    expect(within(unknown!).getAllByRole('cell')[5]).toHaveTextContent('—');
    expect(within(unknown!).getByText('Privé')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('the add link is always shown, even when no row is writable', async () => {
    serve(propertyList([propertySummary({ canWrite: false })]));
    await renderApp('/properties?tab=list');

    await rows();
    expect(screen.getByRole('link', { name: 'Ajouter un bien' })).toBeInTheDocument();
  });

  it('offers « Ajouter un bien » above the list', async () => {
    serve(propertyList([propertySummary()]));
    await renderApp('/properties?tab=list');

    await rows();
    expect(screen.getByRole('link', { name: 'Ajouter un bien' })).toHaveAttribute('href', '/properties/new');
  });

  it('invites to add the first property when there is none', async () => {
    serve(propertyList([]));
    const { container } = await renderApp('/properties?tab=list');

    expect(await screen.findByRole('heading', { name: 'Aucun bien pour l’instant' })).toBeInTheDocument();
    for (const link of screen.getAllByRole('link', { name: 'Ajouter un bien' })) expect(link).toHaveAttribute('href', '/properties/new');
    await expectNoAxeViolations(container);
  });

  it('shows a skeleton while the list loads', async () => {
    server.use(http.get('*/api/v1/realestate/properties', async () => { await delay('infinite'); return HttpResponse.json(propertyList([])); }));
    await renderApp('/properties?tab=list');

    expect(await screen.findByLabelText('Chargement des biens')).toHaveAttribute('aria-busy', 'true');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/realestate/properties', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/properties?tab=list');

    expect(await screen.findByRole('alert', {})).toBeInTheDocument();
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve(propertyList([propertySummary()]), seen);
    await renderApp('/properties?tab=list&scope=me');

    await rows();
    expect(seen).toEqual(['?scope=me']);
  });
});
