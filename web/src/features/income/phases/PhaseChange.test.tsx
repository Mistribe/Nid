import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import type { IncomeSourceDetail } from '../api';
import { detail, eur, phase, SALARY } from '../testData';

type ChangeBody = components['schemas']['IncomePhaseChangeBody'];

/** Every month between two ISO dates, as numbers (a test helper; the app's own lives in expected/months.ts). */
function monthsBetween(from: string, to: string) {
  const out: { year: number; month: number }[] = [];
  let [y, m] = [Number(from.slice(0, 4)), Number(from.slice(5, 7))];
  const [endY, endM] = [Number(to.slice(0, 4)), Number(to.slice(5, 7))];
  while (y < endY || (y === endY && m <= endM)) {
    out.push({ year: y, month: m });
    m += 1;
    if (m === 13) { m = 1; y += 1; }
  }
  return out;
}

function serve(initial: IncomeSourceDetail = detail(), dates: (y: number, m: number) => { phaseId: string; on: string }[] = (y, m) => [
  { phaseId: 'p1', on: `${y}-${String(m).padStart(2, '0')}-01` },
  ...(m === 12 ? [{ phaseId: 'p2', on: `${y}-12-15` }] : []),
]) {
  let current = initial;
  const sent: ChangeBody[] = [];
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/income/sources/:id/expected', ({ request, params }) => {
      const url = new URL(request.url);
      const from = url.searchParams.get('from')!;
      const to = url.searchParams.get('to')!;
      return HttpResponse.json({
        id: params.id, from, to,
        months: monthsBetween(from, to).map(({ year, month }) => ({
          year, month, oneOffs: [], total: eur('0'),
          occurrences: dates(year, month).map((d) => ({ ...d, label: d.phaseId === 'p1' ? 'Salaire de base' : 'Prime annuelle', amount: eur('2300.00') })),
        })),
      });
    }),
    http.post('*/api/v1/income/sources/:id/phase-changes', async ({ request }) => {
      const body = (await request.json()) as ChangeBody;
      sent.push(body);
      current = {
        ...current,
        phases: [
          ...current.phases.map((p) => (p.label === body.label && p.status === 'running' ? { ...p, endsOn: body.effectiveOn ?? null } : p)),
          phase({ id: 'p10', label: body.label!, startsOn: body.effectiveOn!, netAmount: body.netAmount!, every: body.every!, status: 'upcoming' }),
        ],
      };
      return HttpResponse.json({ id: 'p10' }, { status: 201 });
    }),
  );
  return { sent };
}

/** The `⋯` of a label's `row`-th phase in the timeline, newest first. */
const menu = (label: string, row = 0) => screen.getAllByRole('button', { name: new RegExp(`^Actions de la période ${label}`) })[row]!;
const findMenu = (label: string) => screen.findAllByRole('button', { name: new RegExp(`^Actions de la période ${label}`) });

async function openChange(user: Awaited<ReturnType<typeof renderApp>>['user'], label = 'Salaire de base', row = 0) {
  await findMenu(label);
  await user.click(menu(label, row));
  await user.click(await screen.findByRole('menuitem', { name: 'Changer le montant' }));
  return screen.findByRole('form', { name: 'Changer le montant' });
}

describe('changing the amount', () => {
  beforeAll(async () => { await import('../IncomeSourceDetailPage'); }, 30_000);

  it('offers the phase’s own payment dates, the next one chosen, and changes the amount at the date picked', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(`/income/${SALARY}`);

    const form = await openChange(user);
    const select = await within(form).findByRole('combobox', { name: 'Date d’effet' });
    await user.click(select);
    const options = within(await screen.findByRole('listbox')).getAllByRole('option');
    expect(options.length).toBeGreaterThan(1);
    expect(options.every((o) => /^1 /.test(o.textContent ?? ''))).toBe(true);
    await user.click(options[0]!);
    expect(within(form).getByLabelText('Montant net')).toHaveValue('2300');
    expect(within(form).queryByLabelText('Libellé')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.clear(within(form).getByLabelText('Montant net'));
    await user.type(within(form).getByLabelText('Montant net'), '2500');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer le changement' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Montant changé.'));
    expect(sent).toEqual([{ label: 'Salaire de base', effectiveOn: expect.stringMatching(/^\d{4}-\d{2}-01$/) as string, netAmount: { amount: '2500', currency: 'EUR' }, every: { count: 1, unit: 'month' } }]);
    expect(screen.getByRole('heading', { name: 'Changements' })).toHaveFocus();
  });

  it('offers « Changer le montant » on running and upcoming phases, never on a past one', async () => {
    serve();
    const { user } = await renderApp(`/income/${SALARY}`);

    await findMenu('Salaire de base');
    await user.click(menu('Salaire de base', 0));
    expect(await screen.findByRole('menuitem', { name: 'Changer le montant' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    await user.click(menu('Salaire de base', 1));
    expect(await screen.findByRole('menuitem', { name: 'Corriger' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Changer le montant' })).not.toBeInTheDocument();
    await user.keyboard('{Escape}');
    await user.click(menu('Prime annuelle', 0));
    expect(await screen.findByRole('menuitem', { name: 'Changer le montant' })).toBeInTheDocument();
  });

  it('says when there is no date to offer, and gives the focus back on « Annuler »', async () => {
    serve(detail(), () => []);
    const { user } = await renderApp(`/income/${SALARY}`);

    const form = await openChange(user);
    expect(await within(form).findByText('Aucune date de versement à proposer pour cette période.')).toBeInTheDocument();
    expect(within(form).queryByLabelText('Date d’effet')).not.toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(menu('Salaire de base')).toHaveFocus());
  });

  it('keeps an open form when a background refetch of the dates fails', async () => {
    serve();
    const { user, queryClient } = await renderApp(`/income/${SALARY}`);

    const form = await openChange(user);
    await within(form).findByLabelText('Date d’effet');
    await user.clear(within(form).getByLabelText('Montant net'));
    await user.type(within(form).getByLabelText('Montant net'), '2500');
    server.use(http.get('*/api/v1/income/sources/:id/expected', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    void queryClient.invalidateQueries({ queryKey: ['get', '/api/v1/income/sources/{id}/expected'] });

    // The refetch has failed (the query keeps its data and turns to « error »), and the form is still there as typed.
    await waitFor(() => expect(queryClient.getQueryCache().findAll({ queryKey: ['get', '/api/v1/income/sources/{id}/expected'] }).some((q) => q.state.status === 'error')).toBe(true));
    expect(within(screen.getByRole('form', { name: 'Changer le montant' })).getByLabelText('Montant net')).toHaveValue('2500');
  });

  it('shows a refusal at the effective date under the select', async () => {
    serve();
    server.use(http.post('*/api/v1/income/sources/:id/phase-changes', () => HttpResponse.json(
      { status: 422, code: 'income.invalid_phase', title: 'x', errors: [{ path: 'effectiveOn', code: 'income.invalid_phase', message: 'x' }] }, { status: 422 })));
    const { user } = await renderApp(`/income/${SALARY}`);

    const form = await openChange(user);
    await within(form).findByLabelText('Date d’effet');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer le changement' }));

    expect(await within(form).findByText('Ce n’est pas une date de versement de cette période.')).toBeInTheDocument();
    expect(within(form).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('says above the form when no phase of that label runs any more', async () => {
    serve();
    server.use(http.post('*/api/v1/income/sources/:id/phase-changes', () => HttpResponse.json(
      { status: 422, code: 'income.no_running_phase', title: 'x', errors: [{ path: 'label', code: 'income.no_running_phase', message: 'x' }] }, { status: 422 })));
    const { user } = await renderApp(`/income/${SALARY}`);

    const form = await openChange(user);
    await within(form).findByLabelText('Date d’effet');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer le changement' }));

    expect(await within(form).findByRole('alert')).toHaveTextContent('Aucune période de ce libellé ne court à cette date.');
  });
});
