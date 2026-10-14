import { screen, within } from '@testing-library/react';
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

async function rows(): Promise<HTMLElement[]> {
  return within(await screen.findByRole('list', { name: 'Liste des biens' })).getAllByRole('listitem');
}

const parking = propertySummary({
  id: '0192f0bb-0000-7000-8000-00000000e002', name: 'Parking gare', type: 'parking', visibility: 'private', value: null,
  valueOn: null, netValue: null,
});

describe('PropertiesPage', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('./PropertiesPage'); }, 30_000);

  it('shows each property: its name linking to its detail, its type, its value today with its date, its net value and access', async () => {
    const seen: string[] = [];
    serve(propertyList([propertySummary()]), seen);
    const { container } = await renderApp('/properties');

    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: 'Maison de Nantes' })).toHaveAttribute('href', `/properties/${PROPERTY}`);
    expect(row).toHaveTextContent('Résidence principale');
    expect(row).toHaveTextContent('Valeur 290 000,00 € au 1 juin 2026');
    expect(row).toHaveTextContent('Valeur nette 96 435,77 €');
    expect(within(row).getByText('Partagé')).toBeInTheDocument();
    expect(row).not.toHaveTextContent('À réestimer');
    expect(seen).toEqual(['?scope=household']);
    await expectNoAxeViolations(container);
  });

  it('marks a stale estimate « À réestimer » in words, and a property never estimated « Pas encore estimé »', async () => {
    serve(propertyList([propertySummary({ stale: true }), parking]));
    const { container } = await renderApp('/properties');

    const [stale, never] = await rows();
    expect(stale).toHaveTextContent('À réestimer');
    expect(stale).toHaveTextContent('Valeur 290 000,00 € au 1 juin 2026');
    expect(never).toHaveTextContent('Pas encore estimé');
    expect(never).not.toHaveTextContent('Valeur');
    expect(within(never!).getByText('Privé')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('offers « Ajouter un bien » above the list', async () => {
    serve(propertyList([propertySummary()]));
    await renderApp('/properties');

    await rows();
    expect(screen.getByRole('link', { name: 'Ajouter un bien' })).toHaveAttribute('href', '/properties/new');
  });

  it('invites to add the first property when there is none', async () => {
    serve(propertyList([]));
    const { container } = await renderApp('/properties');

    expect(await screen.findByRole('heading', { name: 'Aucun bien pour l’instant' })).toBeInTheDocument();
    for (const link of screen.getAllByRole('link', { name: 'Ajouter un bien' })) expect(link).toHaveAttribute('href', '/properties/new');
    await expectNoAxeViolations(container);
  });

  it('shows a skeleton while the list loads', async () => {
    server.use(http.get('*/api/v1/realestate/properties', async () => { await delay('infinite'); return HttpResponse.json(propertyList([])); }));
    await renderApp('/properties');

    expect(await screen.findByLabelText('Chargement des biens')).toHaveAttribute('aria-busy', 'true');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/realestate/properties', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/properties');

    expect(await screen.findByRole('alert', {}, { timeout: 8000 })).toBeInTheDocument();
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve(propertyList([propertySummary()]), seen);
    await renderApp('/properties?scope=me');

    await rows();
    expect(seen).toEqual(['?scope=me']);
  });
});
