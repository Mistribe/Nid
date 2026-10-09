import { act, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { RoutineView } from './api';
import { LIVRET, MANUEL, line, recap, recapLine, routine } from './testData';

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

describe('the routine', () => {
  it('lists the due envelopes with last month’s value proposed, the others folded', async () => {
    serve(routine(lines()));
    const { container } = await renderApp('/routine');

    const due = await screen.findByRole('region', { name: 'À relever' });
    expect(screen.getByRole('heading', { level: 1, name: 'Routine de septembre 2025' })).toBeInTheDocument();
    expect(within(due).getByLabelText('Valeur de PEA')).toHaveValue('1000.00');
    expect(within(due).getByText('1 000,00 € au 31 août 2025')).toBeInTheDocument();
    expect(within(due).getByLabelText('Valeur de Livret A')).toHaveValue('');
    expect(screen.getByText('Autres enveloppes').closest('details')).not.toHaveAttribute('open');
    expect(screen.getByRole('button', { name: 'Enregistrer 1 enveloppe' })).toBeEnabled();
    await expectNoAxeViolations(container);
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
    await user.click(screen.getByRole('button', { name: 'Enregistrer 2 enveloppes' }));

    await expect.poll(() => posted.length).toBe(1);
    expect(posted[0]).toEqual({
      on: '2025-09-30',
      lines: [
        { envelopeId: '00000000-0000-7000-8000-0000000000f1', value: { amount: '1000.00', currency: 'EUR' }, contribution: { amount: '50', currency: 'EUR' } },
        { envelopeId: LIVRET, value: { amount: '300.5', currency: 'EUR' }, contribution: null },
      ],
    });
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
    await user.click(screen.getByRole('button', { name: /Enregistrer/ }));
    expect(await screen.findByText('Un montant comme 1200 ou 1200,50')).toBeInTheDocument();

    await user.clear(screen.getByLabelText('Versements du mois sur PEA'));
    await user.type(screen.getByLabelText('Valeur de Livret A'), '10');
    await user.type(screen.getByLabelText('Versements du mois sur Livret A'), '5');
    await user.click(screen.getByRole('button', { name: /Enregistrer/ }));
    // lines[1] of what was sent is Livret A (PEA is lines[0]).
    const livret = screen.getByLabelText('Versements du mois sur Livret A');
    expect(await screen.findByText('Montant invalide.')).toBeInTheDocument();
    expect(livret).toHaveAttribute('aria-invalid', 'true');
  });

  it('says when everything is up to date and opens the others', async () => {
    serve(routine([line({ due: false, lastAsOf: '2025-09-30' })]));
    await renderApp('/routine');
    expect(await screen.findByText('Tout est à jour pour septembre 2025.')).toBeInTheDocument();
    expect(screen.getByText('Autres enveloppes').closest('details')).toHaveAttribute('open');
    expect(screen.getByRole('button', { name: 'Rien à enregistrer' })).toBeDisabled();
  });

  it('keeps the chosen date in the URL', async () => {
    const seen: string[] = [];
    server.use(http.get('*/api/v1/investments/routine', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(routine(lines(), '2025-08-31')); }));
    await renderApp('/routine?on=2025-08-31');
    expect(await screen.findByRole('heading', { level: 1, name: 'Routine de août 2025' })).toBeInTheDocument();
    expect(seen[0]).toContain('on=2025-08-31');
  });

  it('refetches the list after a save', async () => {
    let gets = 0;
    serve(routine(lines()));
    server.use(http.get('*/api/v1/investments/routine', () => { gets += 1; return HttpResponse.json(routine(lines())); }));
    const { user } = await renderApp('/routine');
    await user.type(await screen.findByLabelText('Versements du mois sur PEA'), '50');
    const before = gets;
    await user.click(screen.getByRole('button', { name: /Enregistrer/ }));
    await expect.poll(() => gets).toBeGreaterThan(before);
  });

  it('does not pin stale values: an untouched form leaves no draft', async () => {
    serve(routine(lines()));
    const first = await renderApp('/routine');
    await screen.findByLabelText('Valeur de PEA');
    first.unmount();
    serve(routine([line({ lastValue: { amount: '1200.00', currency: 'EUR' }, lastAsOf: '2025-09-15' })]));
    await renderApp('/routine');
    expect(await screen.findByLabelText('Valeur de PEA')).toHaveValue('1200.00');
  });

  it('ignores a malformed draft', async () => {
    sessionStorage.setItem('routine:2025-09-30', '{}');
    serve(routine(lines()));
    await renderApp('/routine');
    expect(await screen.findByLabelText('Valeur de PEA')).toHaveValue('1000.00');
  });
  it('shows the recap with the gain of the month', async () => {
    serve(routine(lines()));
    const { user, container } = await renderApp('/routine');
    await user.click(await screen.findByRole('button', { name: /Enregistrer/ }));

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
    await user.click(screen.getByRole('button', { name: /Enregistrer/ }));

    await user.click(await screen.findByRole('button', { name: 'Annuler' }));

    await expect.poll(() => undone.length).toBe(1);
    expect(undone[0]).toEqual({ facts: [{ envelopeId: '00000000-0000-7000-8000-0000000000f1', valuationId: '00000000-0000-7000-8000-0000000000a9', flowId: null }] });
    expect(await screen.findByText('Routine annulée.')).toBeInTheDocument();
    expect(screen.getByLabelText('Versements du mois sur PEA')).toHaveValue('50');
  });

  it('Annuler disappears after 10 seconds', async () => {
    // Combine with the date pin of beforeEach: fake the timers too, keep the pinned date.
    vi.useRealTimers();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date(2025, 9, 15, 12));
    serve(routine(lines()));
    const { user } = await renderApp('/routine');
    await user.click(await screen.findByRole('button', { name: /Enregistrer/ }));
    expect(await screen.findByRole('button', { name: 'Annuler' })).toBeInTheDocument();
    act(() => { vi.advanceTimersByTime(10_000); });
    expect(screen.queryByRole('button', { name: 'Annuler' })).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Routine enregistrée.');
  });
});
