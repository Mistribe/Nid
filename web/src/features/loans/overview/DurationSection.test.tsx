import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { LoansOverview } from '../api';
import { ALICE, BOB, CAR, household } from '../testData';

const serve = (body: LoansOverview) => server.use(http.get('*/api/v1/realestate/loans/overview', () => HttpResponse.json(body)));
const region = (name: string) => screen.findByRole('region', { name });
const persons = [
  { id: ALICE, kind: 'adult', displayName: 'Alice', email: null },
  { id: BOB, kind: 'adult', displayName: 'Bob', email: null },
];

/** The left edge of each phase of a loan's bar, in percent of the axis, in the drawing's order. */
const phases = (container: HTMLElement) => [...container.querySelectorAll<HTMLElement>('[data-phase]')];

describe('Crédits › durée et mensualité du foyer', () => {
  beforeAll(async () => { await import('../LoansPage'); await import('./OverviewTab'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 9, 21, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('draws each loan from its start to its end, a deferral hatched and named', async () => {
    serve(household());
    const { container } = await renderApp('/loans');

    await region('Durée des crédits et mensualité du foyer');
    const drawn = phases(container);
    expect(drawn.map((p) => p.dataset.phase)).toEqual(['annuity', 'deferred_partial', 'annuity']);
    expect(drawn[1]!.style.background).toContain('repeating-linear-gradient');
    const labels = [...container.querySelectorAll<HTMLElement>('[data-phase-label]')];
    expect(labels[1]).toHaveTextContent('Différé partiel (intérêts seuls)');
    expect(labels[0]).toHaveTextContent('Échéances constantes à 3,5 %');
    // « Tout »: the axis starts at the car's disbursement (1 January 2024), just before its first payment.
    expect(parseFloat(drawn[1]!.style.left)).toBeLessThan(1);
    expect(parseFloat(drawn[1]!.style.left)).toBeLessThan(parseFloat(drawn[0]!.style.left));
    await expectNoAxeViolations(container);
  });

  it('bounds the axis from today with ?span=, and the choice goes to the URL', async () => {
    serve(household());
    const { container, user, router } = await renderApp('/loans');

    await user.click(within(await region('Durée des crédits et mensualité du foyer')).getByRole('radio', { name: '5 ans' }));

    await waitFor(() => expect(router.state.location.search).toMatchObject({ span: '5y' }));
    // The car's phases are all before today: clamped to the axis' start, with no width.
    await waitFor(() => expect(phases(container)[2]!.style.width).not.toBe('0%'));
    expect(phases(container)[1]!.style.width).toBe('0%');
    expect(phases(container)[0]!.style.left).toBe('0%');
  });

  it('starts on « 10 ans » from the URL', async () => {
    serve(household());
    await renderApp('/loans?span=10y');

    expect(within(await region('Durée des crédits et mensualité du foyer')).getByRole('radio', { name: '10 ans' })).toBeChecked();
  });

  it('lists each loan with its start, end and running phase on a phone, and the steps', async () => {
    serve(household());
    await renderApp('/loans');

    const section = await region('Durée des crédits et mensualité du foyer');
    const car = within(section).getByRole('link', { name: /Prêt auto/ });
    expect(car).toHaveAttribute('href', `/loans/${CAR}`);
    expect(car).toHaveTextContent('1 janvier 2024 – 15 janvier 2027');
    expect(car).toHaveTextContent('Phase en cours : Échéances constantes à 0 %');
    const steps = within(section).getByText('octobre 2026 – janvier 2027').closest('li')!;
    expect(steps).toHaveTextContent('2 159,92 €');
  });

  it('gives the spans and the steps as tables', async () => {
    serve(household());
    await renderApp('/loans');

    const spans = await screen.findByRole('table', { name: 'Durée de chaque crédit et ses phases' });
    expect(within(spans).getAllByRole('row')[2]).toHaveTextContent(
      'Prêt auto1 janvier 202415 janvier 2027Différé partiel (intérêts seuls), du 15 janvier 2024 au 15 mars 2024 ; Échéances constantes à 0 %, du 15 avril 2024 au 15 janvier 2027');
    const steps = screen.getByRole('table', { name: 'Mensualité du foyer par palier' });
    expect(within(steps).getAllByRole('row').slice(1).map((r) => r.textContent?.replace(/\s/g, ' '))).toEqual([
      'octobre 2026janvier 20272 159,92 €', 'février 2027octobre 20451 159,92 €',
    ]);
  });

  it('shows each person’s share of each loan', async () => {
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)));
    serve(household());
    await renderApp('/loans');

    const section = await region('Part de chacun');
    const alice = await within(section).findByRole('region', { name: 'Alice' });
    expect(within(alice).getByRole('heading', { level: 3, name: 'Alice' })).toBeInTheDocument();
    expect(alice).toHaveTextContent('Restant dû102 782,12 €');
    expect(alice).toHaveTextContent('Mensualité1 579,96 €');
    expect(alice).toHaveTextContent('Prêt maison · 50 %96 782,12 €');
    expect(alice).toHaveTextContent('Prêt auto · 100 %6 000,00 €');
    expect(within(section).getByRole('region', { name: 'Bob' })).toHaveTextContent('Prêt maison · 50 %96 782,11 €');
  });
});
