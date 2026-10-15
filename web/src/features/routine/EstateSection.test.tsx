import { act, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { EstateRoutineView } from './api';
import {
  FLOW, LOCATIF, MAISON, VALUATION, cost, estateRecap, estateRecapLine, estateRoutine, estimate, line, recap, recapLine, rent, routine,
} from './testData';

const INSURANCE = '00000000-0000-7000-8000-0000000000c2';

/** Maison is due, Locatif is up to date; Maison's tax and Locatif's rent fall in September. */
const september = () => estateRoutine({
  estimates: [estimate(), estimate({ property: LOCATIF, name: 'Locatif', lastValue: '180000.00', lastAsOf: '2025-06-30', due: false })],
  costs: [cost()],
  rents: [rent()],
});

const refusal = (path: string, code: string, title: string) =>
  HttpResponse.json({ status: 422, code, title, errors: [{ path, code, message: title }] }, { status: 422 });

function serve(view: EstateRoutineView, posted: unknown[] = [], answer: () => Response = () => HttpResponse.json(estateRecap([estateRecapLine()]))) {
  server.use(
    http.get('*/api/v1/investments/routine', () => HttpResponse.json(routine([line()]))),
    http.get('*/api/v1/realestate/routine', () => HttpResponse.json(view)),
    http.post('*/api/v1/realestate/routine', async ({ request }) => { posted.push(await request.json()); return answer(); }),
  );
}

const section = () => screen.findByRole('region', { name: 'Biens' });

// The default date is the last day of last month: pin today so it is 2025-09-30.
beforeEach(() => { sessionStorage.clear(); vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2025, 9, 15, 12)); });
afterEach(() => { sessionStorage.clear(); vi.useRealTimers(); });

// The route's lazy chunk loads cold on the first render; warm it so no findBy races its 1 s default.
beforeAll(async () => { await import('./RoutinePage'); }, 30_000);

describe('the properties’ routine', () => {
  it('shows the three groups: estimates empty with their latest, costs and rents unticked', async () => {
    serve(september());
    const { container } = await renderApp('/routine');
    const biens = await section();

    await waitFor(() => expect(within(biens).getByLabelText('Estimation de Maison')).toHaveValue(''));
    expect(within(biens).getByText('Dernière : 290 000,00 € au 30 juin 2024')).toBeInTheDocument();
    expect(within(biens).getByText('Autres biens').closest('details')).not.toHaveAttribute('open');
    const costs = within(biens).getByRole('region', { name: 'Charges du mois' });
    expect(within(costs).getByLabelText('Taxe foncière de Maison')).not.toBeChecked();
    expect(within(costs).getByLabelText('Montant : Taxe foncière de Maison')).toHaveValue('1200.00');
    expect(within(costs).getByLabelText('Date : Taxe foncière de Maison')).toHaveValue('2025-09-15');
    const rents = within(biens).getByRole('region', { name: 'Loyers du mois' });
    expect(within(rents).getByLabelText('Loyer T2 de Locatif')).not.toBeChecked();
    expect(within(rents).getByLabelText('Montant : Loyer T2 de Locatif')).toHaveValue('950.00');
    expect(within(biens).getByRole('button', { name: 'Enregistrer les biens' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Enregistrer les placements' })).toBeEnabled();
    await expectNoAxeViolations(container);
  });

  it('says « Déjà saisi ce mois-ci » in words beside its icon', async () => {
    serve(estateRoutine({ estimates: [estimate()], costs: [cost({ alreadyEntered: '1150.00' })] }));
    const { container } = await renderApp('/routine');
    const biens = await section();

    await waitFor(() => expect(within(biens).getByText('Déjà saisi ce mois-ci : 1 150,00 €')).toBeInTheDocument());
    await expectNoAxeViolations(container);
  });

  it('sends the filled estimates and the ticked lines only; « Tout cocher » ticks its group', async () => {
    const posted: unknown[] = [];
    serve(september(), posted);
    const { user, container } = await renderApp('/routine');
    const biens = await section();

    await user.type(await within(biens).findByLabelText('Estimation de Maison'), '300000,5');
    await user.click(within(within(biens).getByRole('region', { name: 'Loyers du mois' })).getByRole('button', { name: 'Tout cocher' }));
    await waitFor(() => expect(within(biens).getByLabelText('Loyer T2 de Locatif')).toBeChecked());
    await user.click(within(biens).getByRole('button', { name: 'Enregistrer les biens' }));

    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toEqual({
      on: '2025-09-30',
      estimates: [{ property: MAISON, amount: '300000.5' }],
      flows: [{ property: LOCATIF, kind: 'income', category: 'rent', occurredOn: '2025-09-01', amount: '950.00' }],
    });
    await expectNoAxeViolations(container);
  });

  it('Enter moves to the next field and never submits', async () => {
    const posted: unknown[] = [];
    serve(september(), posted);
    const { user, container } = await renderApp('/routine');
    const biens = await section();

    await user.click(await within(biens).findByLabelText('Estimation de Maison'));
    await user.keyboard('{Enter}');
    await waitFor(() => expect(within(biens).getByLabelText('Taxe foncière de Maison')).toHaveFocus());
    await user.keyboard('{Enter}');
    await waitFor(() => expect(within(biens).getByLabelText('Montant : Taxe foncière de Maison')).toHaveFocus());
    expect(posted).toHaveLength(0);
    await expectNoAxeViolations(container);
  });

  it('refuses a malformed amount before sending', async () => {
    const posted: unknown[] = [];
    serve(september(), posted);
    const { user } = await renderApp('/routine');
    const biens = await section();

    await user.type(await within(biens).findByLabelText('Estimation de Maison'), '12,345');
    await user.click(within(biens).getByRole('button', { name: 'Enregistrer les biens' }));

    await waitFor(() => expect(within(biens).getByLabelText('Estimation de Maison')).toHaveAttribute('aria-invalid', 'true'));
    expect(within(biens).getByText('Un montant positif, avec deux décimales au plus, comme 1500,50.')).toBeInTheDocument();
    expect(posted).toHaveLength(0);
  });

  it('in the current month, refuses a line dated after today before sending, and sends it dated today', async () => { // Review Focus #3
    const posted: unknown[] = [];
    serve(estateRoutine({ on: '2025-10-15', estimates: [estimate()], costs: [cost({ dueOn: '2025-10-20' })] }), posted);
    const { user, container } = await renderApp('/routine?on=2025-10-15');
    const biens = await section();

    await user.click(await within(biens).findByLabelText('Taxe foncière de Maison'));
    await user.click(within(biens).getByRole('button', { name: 'Enregistrer les biens' }));
    await waitFor(() => expect(within(biens).getByText('Une date du mois, au plus tard aujourd’hui.')).toBeInTheDocument());
    expect(posted).toHaveLength(0);

    await user.clear(within(biens).getByLabelText('Date : Taxe foncière de Maison'));
    await user.type(within(biens).getByLabelText('Date : Taxe foncière de Maison'), '2025-10-15');
    await user.click(within(biens).getByRole('button', { name: 'Enregistrer les biens' }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toMatchObject({ on: '2025-10-15', flows: [{ occurredOn: '2025-10-15' }] });
    await expectNoAxeViolations(container);
  });

  it('places a server refusal under its own line', async () => {
    serve(september(), [], () => refusal('estimates[0].amount', 'realestate.routine_value_exists', 'This property already has an estimate at this date.'));
    const { user } = await renderApp('/routine');
    const biens = await section();

    await user.type(await within(biens).findByLabelText('Estimation de Maison'), '300000');
    await user.click(within(biens).getByRole('button', { name: 'Enregistrer les biens' }));

    await waitFor(() => expect(within(biens).getByLabelText('Estimation de Maison')).toHaveAttribute('aria-invalid', 'true'));
    expect(within(biens).getByText('Une estimation existe déjà à cette date : corrigez-la depuis la fiche du bien.')).toBeInTheDocument();
  });

  it('a refused save of the properties leaves the placements as typed, and they still save', async () => { // Review Focus #5
    const placements: unknown[] = [];
    serve(september(), [], () => refusal('flows[0].amount', 'realestate.invalid_property_field', 'An amount is positive.'));
    server.use(http.post('*/api/v1/investments/routine', async ({ request }) => {
      placements.push(await request.json());
      return HttpResponse.json(recap([recapLine()]));
    }));
    const { user } = await renderApp('/routine');
    await user.type(await screen.findByLabelText('Versements du mois sur PEA'), '50');
    const biens = await section();
    await user.click(await within(biens).findByLabelText('Loyer T2 de Locatif'));
    await user.click(within(biens).getByRole('button', { name: 'Enregistrer les biens' }));

    await waitFor(() => expect(within(biens).getByLabelText('Montant : Loyer T2 de Locatif')).toHaveAttribute('aria-invalid', 'true'));
    expect(within(biens).getByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(screen.getByLabelText('Versements du mois sur PEA')).toHaveValue('50');
    await user.click(screen.getByRole('button', { name: 'Enregistrer les placements' }));
    await waitFor(() => expect(placements).toHaveLength(1));
  });

  it('keeps the changed lines in the tab and restores them over the month’s new lines', async () => { // Review Focus #4
    serve(september());
    const first = await renderApp('/routine');
    const biens = await section();
    await first.user.click(await within(biens).findByLabelText('Loyer T2 de Locatif'));
    await first.user.clear(within(biens).getByLabelText('Montant : Loyer T2 de Locatif'));
    await first.user.type(within(biens).getByLabelText('Montant : Loyer T2 de Locatif'), '900');
    first.unmount();

    // Meanwhile the tax's planned amount moved and an insurance came due: neither was touched, both read as served now.
    serve(estateRoutine({
      estimates: september().estimates,
      costs: [cost({ amount: '1300.00' }), cost({ costId: INSURANCE, category: 'insurance', amount: '200.00', dueOn: '2025-09-20' })],
      rents: [rent()],
    }));
    const { container } = await renderApp('/routine');
    const again = await section();

    await waitFor(() => expect(within(again).getByLabelText('Loyer T2 de Locatif')).toBeChecked());
    expect(within(again).getByLabelText('Montant : Loyer T2 de Locatif')).toHaveValue('900');
    expect(within(again).getByLabelText('Montant : Taxe foncière de Maison')).toHaveValue('1300.00');
    expect(within(again).getByLabelText('Assurance de Maison')).not.toBeChecked();
    await expectNoAxeViolations(container);
  });

  it('ignores a malformed draft', async () => {
    sessionStorage.setItem('routine-estate:2025-09-30', '{"estimates":[{"property":1}]}');
    serve(september());
    const { container } = await renderApp('/routine');
    const biens = await section();

    await waitFor(() => expect(within(biens).getByLabelText('Estimation de Maison')).toHaveValue(''));
    await expectNoAxeViolations(container);
  });

  it('shows the recap, then « Annuler » voids every fact and brings the values back', async () => {
    const undone: unknown[] = [];
    serve(september(), [], () => HttpResponse.json(estateRecap([
      estateRecapLine(),
      estateRecapLine({ property: LOCATIF, name: 'Locatif', valuationId: null, estimate: null,
        flows: [{ flowId: FLOW, kind: 'income', category: 'rent', occurredOn: '2025-09-01', amount: '950.00' }] }),
    ])));
    server.use(http.post('*/api/v1/realestate/routine/undo', async ({ request }) => {
      undone.push(await request.json());
      return HttpResponse.json({ voided: 2 });
    }));
    const { user, container } = await renderApp('/routine');
    const biens = await section();
    await user.type(await within(biens).findByLabelText('Estimation de Maison'), '300000');
    await user.click(within(biens).getByLabelText('Loyer T2 de Locatif'));
    await user.click(within(biens).getByRole('button', { name: 'Enregistrer les biens' }));

    await waitFor(() => expect(within(biens).getByRole('status')).toHaveTextContent('Biens enregistrés.'));
    const table = within(biens).getByRole('table', { name: 'Récapitulatif des biens' });
    expect(within(table).getByRole('row', { name: /Maison/ })).toHaveTextContent('300 000,00 €');
    expect(within(table).getByRole('row', { name: /Locatif/ })).toHaveTextContent(/Loyer : 950,00 €/);
    await expectNoAxeViolations(container);

    await user.click(within(biens).getByRole('button', { name: 'Annuler' }));
    await waitFor(() => expect(undone).toHaveLength(1));
    expect(undone[0]).toEqual({ facts: [{ property: MAISON, valuationId: VALUATION }, { property: LOCATIF, flowId: FLOW }] });
    await waitFor(() => expect(within(biens).getByRole('status')).toHaveTextContent('Saisie des biens annulée.'));
    expect(within(biens).getByLabelText('Estimation de Maison')).toHaveValue('300000');
    expect(within(biens).getByLabelText('Loyer T2 de Locatif')).toBeChecked();
  });

  it('« Annuler » disappears after 10 seconds', async () => {
    // Combine with the date pin of beforeEach: fake the timers too, keep the pinned date.
    vi.useRealTimers();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date(2025, 9, 15, 12));
    serve(september());
    const { user, container } = await renderApp('/routine');
    const biens = await section();
    await user.type(await within(biens).findByLabelText('Estimation de Maison'), '300000');
    await user.click(within(biens).getByRole('button', { name: 'Enregistrer les biens' }));

    await waitFor(() => expect(within(biens).getByRole('button', { name: 'Annuler' })).toBeInTheDocument());
    act(() => { vi.advanceTimersByTime(10_000); });
    expect(within(biens).queryByRole('button', { name: 'Annuler' })).not.toBeInTheDocument();
    expect(within(biens).getByRole('status')).toHaveTextContent('Biens enregistrés.');
    await expectNoAxeViolations(container);
  });

  it('without any property says there is nothing to enter and offers « Ajouter un bien »', async () => {
    serve(estateRoutine());
    const { container } = await renderApp('/routine');
    const biens = await section();

    await waitFor(() => expect(within(biens).getByText(/Rien à saisir pour vos biens ce mois-ci\./)).toBeInTheDocument());
    expect(within(biens).getByRole('link', { name: 'Ajouter un bien' })).toHaveAttribute('href', '/properties/new');
    expect(within(biens).queryByRole('button', { name: 'Enregistrer les biens' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('with nothing due says so and opens the other estimates', async () => {
    serve(estateRoutine({ estimates: [estimate({ due: false, lastAsOf: '2025-06-30' })] }));
    const { container } = await renderApp('/routine');
    const biens = await section();

    await waitFor(() => expect(within(biens).getByText('Rien à saisir pour vos biens ce mois-ci.')).toBeInTheDocument());
    expect(within(biens).getByText('Autres biens').closest('details')).toHaveAttribute('open');
    expect(within(biens).getByRole('button', { name: 'Enregistrer les biens' })).toBeDisabled();
    await expectNoAxeViolations(container);
  });

  it('is not shown without the real estate module', async () => {
    let reads = 0;
    serve(september());
    server.use(http.get('*/api/v1/realestate/routine', () => { reads += 1; return HttpResponse.json(september()); }));
    const { container } = await renderApp('/routine', {
      capabilities: { ...allCapabilities, modules: allCapabilities.modules.filter((m) => m !== 'realestate') },
    });

    await waitFor(() => expect(screen.getByRole('button', { name: 'Enregistrer les placements' })).toBeEnabled());
    expect(screen.queryByRole('region', { name: 'Biens' })).not.toBeInTheDocument();
    expect(reads).toBe(0);
    await expectNoAxeViolations(container);
  });

  it('with the real estate module alone, the nav reaches the routine, which shows « Biens » and asks for no envelopes', async () => {
    let envelopeReads = 0;
    serve(september());
    server.use(http.get('*/api/v1/investments/routine', () => { envelopeReads += 1; return HttpResponse.json(routine([line()])); }));
    const { user, container } = await renderApp('/properties', { capabilities: { modules: ['realestate'], admin: false } });

    await user.click(await screen.findByRole('link', { name: 'Routine' }));
    const biens = await section();
    await waitFor(() => expect(within(biens).getByLabelText('Estimation de Maison')).toHaveValue(''));
    expect(screen.queryByRole('button', { name: 'Enregistrer les placements' })).not.toBeInTheDocument();
    expect(envelopeReads).toBe(0);
    await expectNoAxeViolations(container);
  });

  it('with the investments module alone, shows the envelopes and no « Biens »', async () => {
    let estateReads = 0;
    serve(september());
    server.use(http.get('*/api/v1/realestate/routine', () => { estateReads += 1; return HttpResponse.json(september()); }));
    const { container } = await renderApp('/routine', { capabilities: { modules: ['investments'], admin: false } });

    await waitFor(() => expect(screen.getByRole('button', { name: 'Enregistrer les placements' })).toBeEnabled());
    expect(screen.queryByRole('region', { name: 'Biens' })).not.toBeInTheDocument();
    expect(estateReads).toBe(0);
    await expectNoAxeViolations(container);
  });
});
