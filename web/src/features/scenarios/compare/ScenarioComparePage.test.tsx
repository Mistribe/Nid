import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { projection, projectionPoint } from '@/features/dashboard/testData';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { ScenarioCompare } from '../api';
import { ALICE, persons, PURCHASE, scenarioList } from '../projection/testData';

const HALF = '0192f0aa-0000-7000-8000-0000000000b2';

/** The base (one warning of its own, Alice's retirement offered) and the half-time projection, 25 000 € ahead at the horizon. */
const compared = (ids: string[]): ScenarioCompare => ({
  base: projection({
    warnings: [{ code: 'income_end_extended', detail: 'Mon CDD' }],
    retirementHorizons: [{ person: ALICE, name: 'Alice', on: '2044-05-31' }],
  }),
  scenarios: ids.map((id) => ({
    id, name: id === PURCHASE ? 'Studio Lyon' : 'Mi-temps', kind: id === PURCHASE ? 'property_purchase' : 'projection',
    result: projection({
      points: [
        projectionPoint('2026-10-27', { nominal: '10000.00', real: '10000.00' }),
        projectionPoint('2026-12-31', { nominal: '12000.00', real: '11500.00' }),
        projectionPoint('2027-12-31', { nominal: '20000.00', real: '19000.00' }),
        projectionPoint('2028-03-31', { nominal: '43000.00', real: '41000.00' }),
      ],
      warnings: id === PURCHASE ? [] : [{ code: 'event_in_past', detail: '1' }],
    }),
    gapNominal: '25000.00', gapReal: '24000.00',
  })),
});

/** Serves the list and the comparison; `seen` collects each comparison's query string. */
const serve = (seen: string[] = [], answer?: (url: URL) => Response) => {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/scenarios', () => HttpResponse.json(scenarioList)),
    http.get('*/api/v1/scenarios/compare', ({ request }) => {
      const url = new URL(request.url);
      seen.push(url.search);
      return answer?.(url) ?? HttpResponse.json(compared(url.searchParams.get('ids')!.split(',')));
    }),
  );
  return seen;
};

const horizonTable = () => screen.findByRole('table', { name: 'Patrimoine à l’horizon' });

describe('ScenarioComparePage', () => {
  beforeAll(async () => { await import('./ScenarioComparePage'); }, 30_000);

  it('asks nothing and invites a choice without ids', async () => {
    const seen = serve();
    const { container } = await renderApp('/scenarios/compare');

    expect(await screen.findByText('Choisissez un scénario à comparer avec la base.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Comparer' })).toBeInTheDocument();
    expect(seen).toEqual([]);
    await expectNoAxeViolations(container);
  });

  it('picking a scenario puts its id in the URL and draws base plus one line', async () => {
    const seen = serve();
    const { user, router } = await renderApp('/scenarios/compare');

    await pick(user, 'Scénario 1', 'Mi-temps');

    await waitFor(() => expect(router.state.location.search).toEqual({ ids: HALF }));
    const legend = await screen.findByRole('list', { name: 'Légende' });
    expect(within(legend).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Base', 'Mi-temps']);
    expect(seen.at(-1)).toContain(`ids=${HALF}`);
    expect(seen.at(-1)).toContain('scope=household');
  });

  it('the second picker does not offer the scenario chosen in the first', async () => {
    serve();
    const { user } = await renderApp(`/scenarios/compare?ids=${HALF}`);

    await user.click(await screen.findByRole('combobox', { name: 'Scénario 2' }));

    const list = await screen.findByRole('listbox');
    expect(within(list).getAllByRole('option').map((o) => o.textContent)).toEqual(['Studio Lyon']);
  });

  it('removing a choice takes it out of the URL', async () => {
    serve();
    const { user, router } = await renderApp(`/scenarios/compare?ids=${HALF},${PURCHASE}`);

    await user.click(await screen.findByRole('button', { name: 'Retirer Mi-temps' }));

    await waitFor(() => expect(router.state.location.search).toEqual({ ids: PURCHASE }));
  });

  it('the horizon segment sets ?horizon= and offers the retirements of the last answer', async () => {
    const seen = serve();
    const { user, router } = await renderApp(`/scenarios/compare?ids=${HALF}`);

    await user.click(await screen.findByRole('radio', { name: 'Retraite de Alice' }));

    await waitFor(() => expect(router.state.location.search).toEqual({ ids: HALF, horizon: `retire:${ALICE}` }));
    await waitFor(() => expect(seen.at(-1)).toContain(`horizon=retire%3A${ALICE}`));
  });

  it('the horizon table shows base and each scenario with its gap, following the reading', async () => {
    serve();
    const { user } = await renderApp(`/scenarios/compare?ids=${HALF},${PURCHASE}`);

    const rows = within(await horizonTable()).getAllByRole('row').slice(1);
    expect(rows.map((r) => r.textContent)).toEqual([
      expect.stringContaining('Base'), expect.stringContaining('Mi-temps'), expect.stringContaining('Studio Lyon'),
    ]);
    expect(rows[0]).toHaveTextContent('17 000,00 €');
    expect(rows[1]).toHaveTextContent('41 000,00 €');
    expect(rows[1]).toHaveTextContent('24 000,00 €');
    expect(within(rows[1]!).getByRole('link', { name: 'Mi-temps' })).toHaveAttribute('href', `/scenarios/projection/${HALF}`);
    expect(within(rows[2]!).getByRole('link', { name: 'Studio Lyon' })).toHaveAttribute('href', `/scenarios/${PURCHASE}`);

    await user.click(screen.getByRole('radio', { name: 'Valeur courante' }));

    expect(rows[1]).toHaveTextContent('43 000,00 €');
    expect(rows[1]).toHaveTextContent('25 000,00 €');
  });

  it('« Afficher en tableau » lists year ends, one column per line', async () => {
    serve();
    const { user } = await renderApp(`/scenarios/compare?ids=${HALF}`);
    await horizonTable();

    await user.click(screen.getByRole('button', { name: 'Afficher en tableau' }));

    const table = await screen.findByRole('table', { name: 'Patrimoine projeté par année' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Année', 'Base', 'Mi-temps']);
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((r) => within(r).getAllByRole('rowheader')[0]!.textContent)).toEqual(['2026', '2027', 'mars 2028']);
    expect(rows[1]).toHaveTextContent('16 100,00 €');
    expect(rows[1]).toHaveTextContent('19 000,00 €');
  });

  it('a base warning shows once under Base, a scenario’s own under its name', async () => {
    serve();
    await renderApp(`/scenarios/compare?ids=${HALF},${PURCHASE}`);
    await horizonTable();

    expect(screen.getAllByText('Le revenu « Mon CDD » est supposé continuer après sa fin.')).toHaveLength(1);
    expect(screen.getAllByText('Un événement daté dans le passé est pris aujourd’hui.')).toHaveLength(1);
    expect(screen.getByText('Chiffres indicatifs : rendements supposés, pas d’impôt futur ni de pension.')).toBeInTheDocument();
  });

  it('a 404 shows the not-found message and keeps the pickers', async () => {
    serve([], () => HttpResponse.json({ status: 404, code: 'NOT_FOUND', title: 'Not found.' }, { status: 404 }));
    await renderApp(`/scenarios/compare?ids=${HALF}`);

    expect(await screen.findByText('Un des scénarios n’existe plus ou n’est pas visible dans cette portée.')).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Scénario 1' })).toBeEnabled();
  });

  it('a refused horizon shows the refusal and keeps the horizon picker', async () => {
    serve([], () => HttpResponse.json(
      { status: 422, code: 'projection.invalid_horizon', title: 'This person has no birth date.' }, { status: 422 }));
    await renderApp(`/scenarios/compare?ids=${HALF}&horizon=retire:${ALICE}`);

    expect(await screen.findByText('This person has no birth date.')).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: '10 ans' })).toBeEnabled();
  });
});
