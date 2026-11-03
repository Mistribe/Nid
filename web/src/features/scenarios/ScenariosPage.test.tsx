import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { ScenarioListItem } from './api';

const ALICE = '0192f0aa-0000-7000-8000-0000000000a1';
const eur = (amount: string) => ({ amount, currency: 'EUR' });

const item = (over: Partial<ScenarioListItem> = {}): ScenarioListItem => ({
  id: '0192f0aa-0000-7000-8000-0000000000b1', kind: 'property_purchase', name: 'Studio Lyon', use: 'rental', price: eur('180000.00'), newPayment: eur('950.00'),
  debtRatioAfter: 31.4, gap: eur('-12000.00'), owners: [{ personId: ALICE, shareBp: 10000 }], visibility: 'shared', ...over,
});

const PROJECTION_ID = '0192f0aa-0000-7000-8000-0000000000b9';
const projection = item({
  id: PROJECTION_ID, kind: 'projection', name: 'Retraite anticipée', use: null, price: null, newPayment: null, debtRatioAfter: null, gap: eur('25000.00'),
});

const serve = (items: ScenarioListItem[]) => {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: ALICE, kind: 'adult', displayName: 'Alice', email: null }])),
    http.get('*/api/v1/scenarios', () => HttpResponse.json({ items })),
  );
};

async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des scénarios' });
  return within(table).getAllByRole('row').slice(1);
}

describe('ScenariosPage', () => {
  beforeAll(async () => { await import('./ScenariosPage'); }, 30_000);

  it('« Comparer » opens the compare page, keeping the scope', async () => {
    serve([item(), projection]);
    await renderApp('/scenarios?scope=me');

    const link = await screen.findByRole('link', { name: 'Comparer' });
    expect(link).toHaveAttribute('href', '/scenarios/compare?scope=me');
  });

  it('lists the scenarios with their figures and owners', async () => {
    serve([item(), item({ id: 'b', name: 'T3 Paris', use: 'primary', debtRatioAfter: null })]);
    await renderApp('/scenarios');

    const table = await screen.findByRole('table', { name: 'Liste des scénarios' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Nom', 'Type', 'Usage', 'Prix', 'Mensualité', 'Endettement', 'Écart à l\'horizon', 'Propriétaire', 'Actions']);
    const items = await rows();
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('Studio Lyon');
    expect(items[0]).toHaveTextContent('Locatif');
    expect(items[0]).toHaveTextContent('180 000,00 €');
    expect(items[0]).toHaveTextContent('31,4 %');
    await waitFor(() => expect(items[0]).toHaveTextContent('Alice'));
    expect(items[1]).toHaveTextContent('Résidence principale');
    expect(items[0]).toHaveTextContent('Achat');
  });

  it('lists projection scenarios beside purchases, each linking to its own page', async () => {
    serve([item(), projection]);
    const { container } = await renderApp('/scenarios');

    const items = await rows();
    expect(items[1]).toHaveTextContent('Retraite anticipée');
    expect(items[1]).toHaveTextContent('Projection');
    expect(items[1]).toHaveTextContent('25 000,00 €');
    expect(within(items[0]!).getByRole('link', { name: /Studio Lyon/ })).toHaveAttribute('href', `/scenarios/${item().id}`);
    expect(within(items[1]!).getByRole('link', { name: /Retraite anticipée/ })).toHaveAttribute('href', `/scenarios/projection/${PROJECTION_ID}`);
    await expectNoAxeViolations(container);
  });

  it('asks which kind of scenario to create', async () => {
    serve([item()]);
    const { user } = await renderApp('/scenarios');

    await rows();
    await user.click(screen.getByRole('button', { name: 'Nouveau scénario' }));
    expect(await screen.findByRole('menuitem', { name: 'Achat immobilier' })).toHaveAttribute('href', '/scenarios/new');
    expect(screen.getByRole('menuitem', { name: 'Projection' })).toHaveAttribute('href', '/scenarios/new?kind=projection');
  });

  it('duplicates a projection scenario through its own routes', async () => {
    serve([projection]);
    const document = { version: 1, horizon: '10y', assumptions: null, events: [] };
    const posted: unknown[] = [];
    server.use(
      http.get('*/api/v1/scenarios/projection/:id', () => HttpResponse.json({
        id: PROJECTION_ID, name: 'Retraite anticipée', visibility: 'private', scope: 'me', canWrite: true, result: { document },
      })),
      http.post('*/api/v1/scenarios/projection', async ({ request }) => { posted.push(await request.json()); return HttpResponse.json({ id: 'new' }, { status: 201 }); }),
      http.get('*/api/v1/scenarios/:id', () => HttpResponse.json({ type: 'about:blank', title: 'Not found', status: 404 }, { status: 404 })),
    );
    const { user } = await renderApp('/scenarios');

    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Retraite anticipée' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Dupliquer' }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toMatchObject({ name: 'Retraite anticipée (copie)', document, visibility: 'private', scope: 'me' });
  });

  it('duplicates a scenario by posting its document under « (copie) »', async () => {
    serve([item()]);
    const document = { version: 1, use: 'rental', price: '180000.00' };
    const posted: unknown[] = [];
    server.use(
      http.get('*/api/v1/scenarios/:id', () => HttpResponse.json({ id: item().id, name: 'Studio Lyon', visibility: 'shared', scope: 'household', document, result: {}, canWrite: true })),
      http.post('*/api/v1/scenarios', async ({ request }) => { posted.push(await request.json()); return HttpResponse.json({ id: 'new' }, { status: 201 }); }),
    );
    const { user } = await renderApp('/scenarios');

    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Studio Lyon' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Dupliquer' }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toMatchObject({ name: 'Studio Lyon (copie)', document, visibility: 'shared', scope: 'household' });
  });

  it('says so when a duplicate is refused', async () => {
    serve([item()]);
    server.use(
      http.get('*/api/v1/scenarios/:id', () => HttpResponse.json({ id: item().id, name: 'Studio Lyon', visibility: 'shared', scope: 'household', document: {}, result: {}, canWrite: true })),
      http.post('*/api/v1/scenarios', () => HttpResponse.json({ type: 'about:blank', title: 'Forbidden', status: 403 }, { status: 403, headers: { 'Content-Type': 'application/problem+json' } })),
    );
    const { user } = await renderApp('/scenarios');

    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Studio Lyon' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Dupliquer' }));
    expect((await screen.findAllByRole('alert')).length).toBeGreaterThan(0);
  });

  it('says so when the scenario to duplicate cannot be read', async () => {
    serve([item()]);
    server.use(http.get('*/api/v1/scenarios/:id', () => HttpResponse.json({ type: 'about:blank', title: 'Not found', status: 404 }, { status: 404, headers: { 'Content-Type': 'application/problem+json' } })));
    const { user } = await renderApp('/scenarios');

    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Studio Lyon' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Dupliquer' }));
    expect((await screen.findAllByRole('alert')).length).toBeGreaterThan(0);
  });

  it('deletes after a confirmation', async () => {
    serve([item()]);
    const deleted: string[] = [];
    server.use(http.delete('*/api/v1/scenarios/:id', ({ params }) => { deleted.push(String(params.id)); return new HttpResponse(null, { status: 204 }); }));
    const { user } = await renderApp('/scenarios');

    await rows();
    await user.click(screen.getAllByRole('button', { name: 'Actions pour Studio Lyon' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    expect(deleted).toEqual([]);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(deleted).toEqual([item().id]));
  });

  it('shows an empty state without scenarios', async () => {
    serve([]);
    await renderApp('/scenarios');

    expect(await screen.findByText('Aucun scénario pour l\'instant')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Nouveau scénario' }).length).toBeGreaterThan(0);
  });
});
