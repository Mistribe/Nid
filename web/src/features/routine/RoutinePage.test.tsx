import { act, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { expectLabelBefore } from '@/test/filters';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { RoutineView } from './api';
import { list, summary } from '../childcare/testData';
import { LIVRET, MANUEL, PEA, line, recap, recapLine, routine } from './testData';

const lines = () => [
  line(),
  line({ id: LIVRET, name: 'Livret A', lastValue: null, lastAsOf: null }),
  line({ id: MANUEL, name: 'Manuel', cadence: 'manual', due: false, lastValue: { amount: '50.00', currency: 'EUR' }, lastAsOf: '2025-06-30' }),
];

function serve(view: RoutineView, posted: unknown[] = []) {
  server.use(
    http.get('*/api/v1/investments/routine', () => HttpResponse.json(view)),
    http.post('*/api/v1/investments/routine', async ({ request }) => {
      posted.push(await request.json());
      return HttpResponse.json(recap([recapLine()]));
    }),
  );
}

// The default date is the last day of last month: pin today so it is 2025-09-30.
beforeEach(() => { sessionStorage.clear(); vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2025, 9, 15, 12)); });
afterEach(() => { sessionStorage.clear(); vi.useRealTimers(); });

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./RoutinePage'); }, 30_000);

describe('the routine', () => {
  it('says how many proposals wait, above the routine', async () => {
    server.use(http.get('*/api/v1/core/proposals', () => HttpResponse.json({ items: [{ id: '1' }] })));
    await renderApp('/routine');

    expect(await screen.findByRole('link', { name: '1 proposition à valider' })).toHaveAttribute('href', '/proposals');
  });

  it('shows nothing about proposals when none wait', async () => {
    serve(routine(lines()));
    let served = false;
    server.use(http.get('*/api/v1/core/proposals', () => { served = true; return HttpResponse.json({ items: [] }); }));
    const { queryClient } = await renderApp('/routine');

    await screen.findByRole('region', { name: 'À relever' });
    await waitFor(() => expect(served).toBe(true));
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(screen.queryByRole('link', { name: /à valider/ })).toBeNull();
  });

  it('lists the due envelopes with last month’s value proposed, the others folded', async () => {
    serve(routine(lines()));
    const { container } = await renderApp('/routine');

    const due = await screen.findByRole('region', { name: 'À relever' });
    expect(screen.getByRole('heading', { level: 1, name: 'Routine de septembre 2025' })).toBeInTheDocument();
    expect(within(due).getByLabelText('Valeur de PEA')).toHaveValue('1000,00');
    expect(within(due).getByText('1 000,00 € au 31 août 2025')).toBeInTheDocument();
    expect(within(due).getByLabelText('Valeur de Livret A')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Autres enveloppes' })).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByLabelText('Valeur de Manuel')).toBeNull();
    expect(screen.getByRole('button', { name: 'Enregistrer les placements' })).toBeEnabled();
    await expectNoAxeViolations(container);
  });

  it('says what a livret can still take, or that it is at its ceiling (issue 399)', async () => {
    serve(routine([
      line({ id: LIVRET, name: 'Livret A', ceilingRoom: { amount: '3600.0000', currency: 'EUR' } }),
      line({ id: PEA, name: 'LDDS', ceilingRoom: { amount: '0', currency: 'EUR' } }),
    ]));
    await renderApp('/routine');
    const due = await screen.findByRole('region', { name: 'À relever' });
    expect(within(due).getByText('Plafond : encore 3 600,00 € versables')).toBeInTheDocument();
    expect(within(due).getByText('Au plafond : plus de versement possible')).toBeInTheDocument();
  });

  it('sends the proposed values, the contributions and skips « Passer »', async () => {
    const posted: unknown[] = [];
    serve(routine(lines()), posted);
    const { user } = await renderApp('/routine');

    await user.type(await screen.findByLabelText('Versements du mois sur PEA'), '50');
    await user.type(screen.getByLabelText('Valeur de Livret A'), '300,5');
    await user.click(screen.getByText('Autres enveloppes'));
    await user.type(screen.getByLabelText('Versements du mois sur Manuel'), '10');
    await user.click(screen.getByLabelText('Passer Manuel'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer les placements' }));

    await expect.poll(() => posted.length).toBe(1);
    expect(posted[0]).toEqual({
      on: '2025-09-30',
      lines: [
        { envelopeId: '00000000-0000-7000-8000-0000000000f1', value: { amount: '1000.00', currency: 'EUR' }, contribution: { amount: '50', currency: 'EUR' } },
        { envelopeId: LIVRET, value: { amount: '300.5', currency: 'EUR' }, contribution: null },
      ],
    });
  });

  it('shows the bank balance instead of the value field of an envelope whose account applies its balances, and sends no value for it', async () => {
    const posted: unknown[] = [];
    serve(routine(lines()), posted);
    server.use(
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: true })),
      http.get('*/api/v1/banking/accounts', () => HttpResponse.json([{
        id: 'b1', name: 'Compte titres', ibanMasked: null, kind: 'other', currency: 'EUR', envelopeId: PEA, cardOfAccountId: null,
        autoApplyBalance: true, ignored: false, balanceConflictOn: null, lastBalance: '1100.00', lastBalanceOn: '2025-09-30', synced: true, canWrite: true,
      }])),
    );
    const { user } = await renderApp('/routine');

    const due = await screen.findByRole('region', { name: 'À relever' });
    expect(await within(due).findByText('Solde bancaire du 30/09')).toBeInTheDocument();
    expect(within(due).queryByLabelText('Valeur de PEA')).toBeNull();
    expect(within(due).getByLabelText('Valeur de Livret A')).toHaveValue('');
    await user.type(screen.getByLabelText('Versements du mois sur PEA'), '20');
    await user.click(screen.getByRole('button', { name: 'Enregistrer les placements' }));

    await expect.poll(() => posted.length).toBe(1);
    expect(posted[0]).toEqual({ on: '2025-09-30', lines: [{ envelopeId: PEA, value: null, contribution: { amount: '20', currency: 'EUR' } }] });
  });

  it('keeps the value field of an envelope whose bank account no connection reads any more', async () => {
    serve(routine(lines()));
    server.use(
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: true })),
      http.get('*/api/v1/banking/accounts', () => HttpResponse.json([{
        id: 'b1', name: 'Compte titres', ibanMasked: null, kind: 'other', currency: 'EUR', envelopeId: PEA, cardOfAccountId: null,
        autoApplyBalance: true, ignored: false, balanceConflictOn: null, lastBalance: '1100.00', lastBalanceOn: '2025-09-30', synced: false, canWrite: true,
      }])),
    );
    await renderApp('/routine');

    const due = await screen.findByRole('region', { name: 'À relever' });
    expect(await within(due).findByLabelText('Valeur de PEA')).toBeInTheDocument();
    expect(within(due).queryByText('Solde bancaire du 30/09')).toBeNull();
  });

  it('Enter moves to the next field and never submits', async () => {
    const posted: unknown[] = [];
    serve(routine(lines()), posted);
    const { user } = await renderApp('/routine');

    await user.click(await screen.findByLabelText('Valeur de PEA'));
    await user.keyboard('{Enter}');
    expect(screen.getByLabelText('Versements du mois sur PEA')).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(screen.getByLabelText('Passer PEA')).toHaveFocus();
    expect(posted).toHaveLength(0);
  });

  it('keeps a draft in the tab and restores it', async () => {
    serve(routine(lines()));
    const first = await renderApp('/routine');
    await first.user.type(await screen.findByLabelText('Versements du mois sur PEA'), '75');
    first.unmount();

    await renderApp('/routine');
    expect(await screen.findByLabelText('Versements du mois sur PEA')).toHaveValue('75');
  });

  it('refuses a malformed amount before sending and places a server refusal under its line', async () => {
    server.use(
      http.get('*/api/v1/investments/routine', () => HttpResponse.json(routine(lines()))),
      http.post('*/api/v1/investments/routine', () => HttpResponse.json(
        { status: 422, code: 'investments.routine_invalid_amount', title: 'A contribution is positive.', errors: [{ path: 'lines[1].contribution', code: 'investments.routine_invalid_amount', message: 'A contribution is positive.' }] },
        { status: 422 })),
    );
    const { user } = await renderApp('/routine');

    await user.type(await screen.findByLabelText('Versements du mois sur PEA'), 'abc');
    await user.click(screen.getByRole('button', { name: 'Enregistrer les placements' }));
    expect(await screen.findByText('Un montant comme 1200 ou 1200,50')).toBeInTheDocument();

    await user.clear(screen.getByLabelText('Versements du mois sur PEA'));
    await user.type(screen.getByLabelText('Valeur de Livret A'), '10');
    await user.type(screen.getByLabelText('Versements du mois sur Livret A'), '5');
    await user.click(screen.getByRole('button', { name: 'Enregistrer les placements' }));
    // lines[1] of what was sent is Livret A (PEA is lines[0]).
    const livret = screen.getByLabelText('Versements du mois sur Livret A');
    expect(await screen.findByText('Montant invalide.')).toBeInTheDocument();
    expect(livret).toHaveAttribute('aria-invalid', 'true');
  });

  it('opens the folded others when a refusal lands on one of their rows', async () => {
    serve(routine(lines()));
    const { user } = await renderApp('/routine');

    await user.click(await screen.findByRole('button', { name: 'Autres enveloppes' }));
    await user.type(screen.getByLabelText('Valeur de Manuel'), 'abc');
    await user.click(screen.getByRole('button', { name: 'Autres enveloppes' }));
    expect(screen.queryByLabelText('Valeur de Manuel')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Enregistrer les placements' }));

    expect(await screen.findByText('Un montant comme 1200 ou 1200,50')).toBeInTheDocument();
    expect(screen.getByLabelText('Valeur de Manuel')).toHaveAttribute('aria-invalid', 'true');
  });

  it('says when everything is up to date and opens the others', async () => {
    const posted: unknown[] = [];
    serve(routine([line({ due: false, lastAsOf: '2025-09-30' })]), posted);
    const { user } = await renderApp('/routine');
    expect(await screen.findByText('Toutes les enveloppes sont à jour pour septembre 2025.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Autres enveloppes' })).toHaveAttribute('aria-expanded', 'true');

    // Never a disabled save (design-system.md §6): the click with nothing filled says so and sends nothing.
    const save = screen.getByRole('button', { name: 'Enregistrer les placements' });
    expect(save).toBeEnabled();
    expect(save.closest('.sticky')).toBeNull();
    await user.click(save);
    expect(await screen.findByRole('status')).toHaveTextContent('Rien à enregistrer : saisissez une valeur ou un versement.');
    expect(posted).toEqual([]);
  });

  it('labels each field shortly, the row naming the envelope; the accessible name still carries it', async () => {
    serve(routine(lines()));
    await renderApp('/routine');

    const due = await screen.findByRole('region', { name: 'À relever' });
    expect(within(due).getAllByText('Valeur', { selector: 'label' }).length).toBeGreaterThan(0);
    expect(within(due).getAllByText('Versements du mois', { selector: 'label' }).length).toBeGreaterThan(0);
    expect(within(due).queryByText(/^Valeur de /, { selector: 'label' })).toBeNull();
    expect(within(due).getByRole('textbox', { name: 'Valeur de PEA' })).toBeInTheDocument();
    expect(within(due).getByRole('checkbox', { name: 'Passer PEA' })).toBeInTheDocument();
  });

  it('puts its date in the filter row, the label before the field', async () => {
    serve(routine(lines()));
    await renderApp('/routine');
    const date = await screen.findByLabelText('Valeurs au');
    const row = screen.getByRole('group', { name: 'Filtres' });
    expectLabelBefore(within(row).getByText('Valeurs au'), date);
    expect(within(row).getByRole('button', { name: 'Choisir une date : Valeurs au' })).toBeInTheDocument();
  });

  it('keeps the chosen date in the URL', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/investments/routine', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(routine(lines(), '2025-08-31')); }));
    await renderApp('/routine?on=2025-08-31');
    expect(await screen.findByRole('heading', { level: 1, name: 'Routine d’août 2025' })).toBeInTheDocument();
    expect(seen[0]).toContain('on=2025-08-31');
  });

  it('opens the typed past date and refuses a later one', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/investments/routine', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(routine(lines(), '2025-08-31')); }));
    const { user, router } = await renderApp('/routine');
    const date = await screen.findByLabelText('Valeurs au');

    await user.clear(date);
    await user.type(date, '31/12/2025');
    expect(await screen.findByText(/Une date passée ou d’aujourd’hui/)).toBeInTheDocument();
    expect(date).toHaveAttribute('aria-invalid', 'true');
    expect(router.state.location.search).not.toHaveProperty('on');

    await user.clear(date);
    await user.type(date, '31/08/2025');
    await waitFor(() => expect(router.state.location.search).toMatchObject({ on: '2025-08-31' }));
    expect(screen.queryByText(/Une date passée ou d’aujourd’hui/)).toBeNull();

    await user.clear(date);
    expect(date).toHaveValue('');
    expect(screen.queryByText(/Une date passée ou d’aujourd’hui/)).toBeNull();
  });

  it('refetches the list after a save', async () => {
    let gets = 0;
    serve(routine(lines()));
    server.use(http.get('*/api/v1/investments/routine', () => { gets += 1; return HttpResponse.json(routine(lines())); }));
    const { user } = await renderApp('/routine');
    await user.type(await screen.findByLabelText('Versements du mois sur PEA'), '50');
    const before = gets;
    await user.click(screen.getByRole('button', { name: 'Enregistrer les placements' }));
    await expect.poll(() => gets).toBeGreaterThan(before);
  });

  it('does not pin stale values: an untouched form leaves no draft', async () => {
    serve(routine(lines()));
    const first = await renderApp('/routine');
    await screen.findByLabelText('Valeur de PEA');
    first.unmount();
    serve(routine([line({ lastValue: { amount: '1200.00', currency: 'EUR' }, lastAsOf: '2025-09-15' })]));
    await renderApp('/routine');
    expect(await screen.findByLabelText('Valeur de PEA')).toHaveValue('1200,00');
  });

  it('ignores a malformed draft', async () => {
    sessionStorage.setItem('routine:2025-09-30', '{}');
    serve(routine(lines()));
    await renderApp('/routine');
    expect(await screen.findByLabelText('Valeur de PEA')).toHaveValue('1000,00');
  });
  it('shows the recap with the gain of the month', async () => {
    serve(routine(lines()));
    const { user, container } = await renderApp('/routine');
    await user.click(await screen.findByRole('button', { name: 'Enregistrer les placements' }));

    const table = await screen.findByRole('table', { name: 'Récapitulatif de la routine' });
    const row = within(table).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('PEA');
    expect(row).toHaveTextContent('1 100,00 €');
    expect(row).toHaveTextContent('50,00 €');
    expect(within(row).getByText('en hausse')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Routine enregistrée.');
    await expectNoAxeViolations(container);
  });

  it('Annuler voids the routine and brings the form back with the values entered', async () => {
    const undone: unknown[] = [];
    serve(routine(lines()));
    server.use(http.post('*/api/v1/investments/routine/undo', async ({ request }) => { undone.push(await request.json()); return HttpResponse.json({ voided: 1 }); }));
    const { user } = await renderApp('/routine');
    await user.type(await screen.findByLabelText('Versements du mois sur PEA'), '50');
    await user.click(screen.getByRole('button', { name: 'Enregistrer les placements' }));

    await user.click(await screen.findByRole('button', { name: 'Annuler' }));

    await expect.poll(() => undone.length).toBe(1);
    expect(undone[0]).toEqual({ facts: [{ envelopeId: '00000000-0000-7000-8000-0000000000f1', valuationId: '00000000-0000-7000-8000-0000000000a9', flowId: null }] });
    expect(await screen.findByText('Routine annulée.')).toBeInTheDocument();
    expect(screen.getByLabelText('Versements du mois sur PEA')).toHaveValue('50');
  });

  it('shows since when the gain runs', async () => {
    server.use(
      http.get('*/api/v1/investments/routine', () => HttpResponse.json(routine(lines()))),
      http.post('*/api/v1/investments/routine', () => HttpResponse.json(recap([recapLine({ previousAsOf: '2025-06-30' })]))),
    );
    const { user } = await renderApp('/routine');
    await user.click(await screen.findByRole('button', { name: 'Enregistrer les placements' }));
    expect(await screen.findByText('depuis le 30 juin 2025')).toBeInTheDocument();
  });

  it('Annuler restores each value under its envelope even when the list is reordered by the save', async () => {
    let saved = false;
    const due = [line(), line({ id: LIVRET, name: 'Livret A', lastValue: null, lastAsOf: null })];
    const after = [line({ id: LIVRET, name: 'Livret A', lastValue: null, lastAsOf: null }), line({ due: false })];
    server.use(
      http.get('*/api/v1/investments/routine', () => HttpResponse.json(routine(saved ? after : due))),
      http.post('*/api/v1/investments/routine', () => { saved = true; return HttpResponse.json(recap([recapLine()])); }),
      http.post('*/api/v1/investments/routine/undo', () => HttpResponse.json({ voided: 1 })),
    );
    const { user } = await renderApp('/routine');
    await user.type(await screen.findByLabelText('Versements du mois sur PEA'), '50');
    await user.click(screen.getByRole('button', { name: 'Enregistrer les placements' }));
    await user.click(await screen.findByRole('button', { name: 'Annuler' }));
    expect(await screen.findByText('Routine annulée.')).toBeInTheDocument();
    // PEA is no longer due after the save: its row is among the folded ones.
    await user.click(screen.getByRole('button', { name: 'Autres enveloppes' }));
    expect(screen.getByLabelText('Versements du mois sur PEA')).toHaveValue('50');
    expect(screen.getByLabelText('Versements du mois sur Livret A')).toHaveValue('');
  });

  it('Annuler disappears after 10 seconds', async () => {
    // Combine with the date pin of beforeEach: fake the timers too, keep the pinned date.
    vi.useRealTimers();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date(2025, 9, 15, 12));
    serve(routine(lines()));
    const { user } = await renderApp('/routine');
    await user.click(await screen.findByRole('button', { name: 'Enregistrer les placements' }));
    expect(await screen.findByRole('button', { name: 'Annuler' })).toBeInTheDocument();
    act(() => { vi.advanceTimersByTime(10_000); });
    expect(screen.queryByRole('button', { name: 'Annuler' })).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Routine enregistrée.');
  });
});

describe('the routine’s childcare', () => {
  it('links the month’s statement to prepare and counts the older ones', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/childcare', ({ request }) => {
      seen.push(new URL(request.url).search);
      return HttpResponse.json(list([summary({ toPrepare: ['2025-09', '2025-07', '2025-06'] }), summary({ id: 'ro', name: 'Lue', canWrite: false, toPrepare: ['2025-09'] })]));
    }));
    await renderApp('/routine');

    const section = await screen.findByRole('region', { name: 'Garde' });
    expect(await within(section).findByRole('link', { name: 'Relevé de septembre 2025 à préparer · Garde de Léa' }))
      .toHaveAttribute('href', `/childcare/${summary().id}/statements/2025-09`);
    expect(within(section).getByRole('link', { name: '2 relevés en retard · Garde de Léa' })).toHaveAttribute('href', `/childcare/${summary().id}`);
    expect(within(section).queryByText(/Lue/)).toBeNull();
    expect(seen).toEqual(['?scope=me&closed=true']);
  });

  it('says when every statement is prepared', async () => {
    await renderApp('/routine');
    expect(await screen.findByText('Tous les relevés de garde sont prêts.')).toBeInTheDocument();
  });

  it('is absent without the childcare module', async () => {
    await renderApp('/routine', { capabilities: { modules: ['core', 'investments', 'realestate'], admin: false } });
    await screen.findByRole('region', { name: 'Biens' });
    expect(screen.queryByRole('region', { name: 'Garde' })).toBeNull();
  });
});
