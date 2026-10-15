import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { DashboardView } from './api';
import { dashboard, envelope, event, person, todoItem } from './testData';

function serve(body: DashboardView) {
  server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(body)));
}

const section = (name: string) => screen.findByRole('region', { name });

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./NetWorthCurve'); await import('./SplitCharts'); }, 30_000);

describe('dashboard lists', () => {
  it('shows each envelope with its converted value, date and staleness, and links to it keeping the scope', async () => {
    serve(dashboard({
      envelopeSplit: [
        envelope(),
        envelope({ id: '00000000-0000-7000-8000-0000000000e3', name: 'Livret A', converted: { amount: '900.00', currency: 'EUR' }, stale: true, asOf: '2025-06-03' }),
        envelope({ id: '00000000-0000-7000-8000-0000000000e4', name: 'Vide', value: null, converted: null, asOf: null }),
      ],
    }));
    const { container, router, user } = await renderApp('/dashboard?scope=me');

    const split = await section('Répartition par élément');
    const rows = within(within(split).getByRole('table', { name: 'Valeur de chaque élément' })).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('PEA');
    expect(rows[1]).toHaveTextContent('25 000,00 €');
    expect(rows[1]).toHaveTextContent('30 septembre 2025');
    expect(rows[2]).toHaveTextContent('À mettre à jour');
    expect(rows[3]).toHaveTextContent('Sans valeur');
    expect(rows[3]).not.toHaveTextContent('0,00 €');
    await expectNoAxeViolations(container);

    await user.click(within(split).getByRole('link', { name: 'PEA' }));
    expect(router.state.location.pathname).toBe('/investments/00000000-0000-7000-8000-0000000000e1');
    expect(router.state.location.search).toEqual({ scope: 'me' });
  });

  it('says « inconnue » for an envelope whose rate is missing and keeps its own amount', async () => {
    serve(dashboard({
      envelopeSplit: [envelope({ name: 'Dollars', value: { amount: '100.00', currency: 'USD' }, converted: null, missingCurrency: 'USD' })],
      people: null, peopleMissingCurrency: 'USD',
    }));
    await renderApp('/dashboard');

    expect(await section('Répartition par élément')).toHaveTextContent('Inconnue : taux de change manquant (USD) · 100,00 $US');
    expect(await section('Répartition par personne')).toHaveTextContent('Inconnue : taux de change manquant (USD)');
  });

  it('shows a property and a loan as lines of their own, the loan negative, each linked to its detail', async () => {
    serve(dashboard({
      envelopeSplit: [
        envelope({ id: '00000000-0000-7000-8000-0000000000f1', module: 'properties', name: 'Maison',
          value: { amount: '310000.00', currency: 'EUR' }, converted: { amount: '310000.00', currency: 'EUR' } }),
        envelope({ id: '00000000-0000-7000-8000-0000000000f2', module: 'loans', name: 'Prêt travaux',
          value: { amount: '-2000.00', currency: 'EUR' }, converted: { amount: '-2000.00', currency: 'EUR' }, asOf: '2025-10-15' }),
      ],
    }));
    const { container } = await renderApp('/dashboard');

    const split = await section('Répartition par élément');
    const table = within(split).getByRole('table', { name: 'Valeur de chaque élément' });
    expect(within(table).getByRole('columnheader', { name: 'Élément' })).toBeInTheDocument();
    const rows = within(table).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('310 000,00 €');
    expect(rows[2]).toHaveTextContent('-2 000,00 €');
    expect(within(split).getByRole('link', { name: 'Maison' })).toHaveAttribute('href', '/properties/00000000-0000-7000-8000-0000000000f1');
    expect(within(split).getByRole('link', { name: 'Prêt travaux' })).toHaveAttribute('href', '/loans/00000000-0000-7000-8000-0000000000f2');
    await expectNoAxeViolations(container);
  });

  it('links a property’s line of « À faire » to the property', async () => {
    serve(dashboard({ todo: [todoItem({ kind: 'no_value', module: 'properties', id: '00000000-0000-7000-8000-0000000000f3', name: 'Terrain', on: null })] }));
    const { container } = await renderApp('/dashboard');

    expect(within(await section('À faire')).getByRole('link', { name: 'Saisir une première valeur pour Terrain' }))
      .toHaveAttribute('href', '/properties/00000000-0000-7000-8000-0000000000f3');
    await expectNoAxeViolations(container);
  });

  it('shows each person’s share with their colour', async () => {
    serve(dashboard({
      envelopeSplit: [envelope()],
      people: [person(), person({ id: '00000000-0000-7000-8000-0000000000a2', name: 'Bob', slot: 1 })],
    }));
    const { container } = await renderApp('/dashboard');

    const rows = within(within(await section('Répartition par personne')).getByRole('table', { name: 'Part de chaque personne' })).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('Alice');
    expect(rows[1]).toHaveTextContent('12 500,00 €');
    expect(rows[1]).toHaveTextContent('50 %');
    expect(rows[2]).toHaveTextContent('Bob');
    await expectNoAxeViolations(container);
  });

  it('hides the split when there is no envelope', async () => {
    serve(dashboard({ envelopes: 0, envelopeSplit: [], people: [] }));
    await renderApp('/dashboard');
    await section('Engagements');
    expect(screen.queryByRole('region', { name: 'Répartition par élément' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Répartition par personne' })).not.toBeInTheDocument();
  });

  it('lists what is coming in the next 60 days, each linked to its item', async () => {
    serve(dashboard({
      upcoming: [
        event(),
        event({ on: '2025-11-30', kind: 'subscription_expiry', name: 'NordVPN', amount: null }),
        event({ on: '2025-12-01', kind: 'income_phase_end', module: 'income', id: '00000000-0000-7000-8000-0000000000b1', name: 'Employeur', label: 'CDD', amount: null }),
      ],
    }));
    const { container } = await renderApp('/dashboard');

    const items = within(await section('À venir')).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('10 novembre 2025');
    expect(items[0]).toHaveTextContent('Amazon Prime');
    expect(items[0]).toHaveTextContent('Renouvellement');
    expect(items[0]).toHaveTextContent('69,90 €');
    expect(items[1]).toHaveTextContent('Expire');
    expect(items[2]).toHaveTextContent('Fin de « CDD »');
    expect(within(items[2] as HTMLElement).getByRole('link', { name: 'Employeur' })).toHaveAttribute('href', '/income/00000000-0000-7000-8000-0000000000b1');
    await expectNoAxeViolations(container);
  });

  it('lists what to do, each linked to its item', async () => {
    serve(dashboard({
      todo: [
        todoItem(),
        todoItem({ kind: 'no_value', id: '00000000-0000-7000-8000-0000000000e5', name: 'PER', on: null }),
        todoItem({ kind: 'income_gap_soon', module: 'income', id: '00000000-0000-7000-8000-0000000000b2', name: 'Employeur', label: 'CDD', on: '2025-11-30' }),
      ],
    }));
    await renderApp('/dashboard');

    const todo = await section('À faire');
    expect(within(todo).getByRole('link', { name: 'Mettre à jour Livret A (dernière valeur le 3 juin 2025)' })).toBeInTheDocument();
    expect(within(todo).getByRole('link', { name: 'Saisir une première valeur pour PER' })).toBeInTheDocument();
    expect(within(todo).getByRole('link', { name: 'Renseigner la suite de « CDD » (Employeur, fin le 30 novembre 2025)' }))
      .toHaveAttribute('href', '/income/00000000-0000-7000-8000-0000000000b2');
  });

  it('says when nothing is coming and nothing is to do', async () => {
    serve(dashboard());
    await renderApp('/dashboard');
    expect(await section('À venir')).toHaveTextContent('Rien dans les 60 prochains jours.');
    expect(await section('À faire')).toHaveTextContent('Rien à faire pour l’instant.');
  });
});
