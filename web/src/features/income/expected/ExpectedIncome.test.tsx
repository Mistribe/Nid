import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { IncomeExpected, IncomeSourceDetail } from '../api';
import { detail, eur, SALARY } from '../testData';
import { expectedRange } from './months';

type Month = IncomeExpected['months'][number];
const month = (year: number, m: number, over: Partial<Month> = {}): Month => ({ year, month: m, occurrences: [], oneOffs: [], total: eur('0'), ...over });

function serve(months: Month[], seen: string[] = [], source: IncomeSourceDetail = detail()) {
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(source)),
    http.get('*/api/v1/income/sources/:id/expected', ({ request, params }) => {
      const url = new URL(request.url);
      seen.push(url.search);
      return HttpResponse.json({ id: params.id, from: url.searchParams.get('from'), to: url.searchParams.get('to'), months });
    }),
  );
}

const QUARTER = [
  month(2026, 10, { occurrences: [{ phaseId: 'q', label: 'Prime trimestrielle', on: '2026-10-15', amount: eur('1000.00') }], total: eur('1000.00') }),
  month(2026, 11),
  month(2026, 12),
];

const groups = async () => {
  const region = await screen.findByRole('region', { name: 'Revenu attendu' });
  const table = await within(region).findByRole('table', { name: 'Revenu attendu, mois par mois' });
  return within(table).getAllByRole('rowgroup').slice(1);
};

describe('expected income', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../IncomeSourceDetailPage'); }, 30_000);

  it('asks for the twelve months from this one, and shows each month’s total as the server gives it', async () => {
    const seen: string[] = [];
    serve(QUARTER, seen);
    const { container } = await renderApp(`/income/${SALARY}`);

    const [october, november, december] = await groups();
    const { from, to } = expectedRange('next12', todayIso());
    expect(seen).toEqual([`?from=${from}&to=${to}`]);
    expect(october).toHaveTextContent('octobre 2026');
    expect(october).toHaveTextContent('Total');
    expect(october).toHaveTextContent('1 000,00 €');
    expect(october).toHaveTextContent('Prime trimestrielle');
    expect(october).toHaveTextContent('15 octobre 2026');
    expect(november).toHaveTextContent('Aucun versement attendu');
    expect(december).toHaveTextContent('Aucun versement attendu');
    const region = screen.getByRole('region', { name: 'Revenu attendu' });
    expect(within(region).getByRole('application', { name: 'Revenu attendu chaque mois' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('names a one-off line with its note', async () => {
    serve([month(2026, 10, { oneOffs: [{ id: 'o1', occurredOn: '2026-10-03', amount: eur('400.00'), note: 'Prime de projet' }], total: eur('400.00') })]);
    await renderApp(`/income/${SALARY}`);

    const [october] = await groups();
    expect(october).toHaveTextContent('Revenu exceptionnel — Prime de projet');
    expect(october).toHaveTextContent('3 octobre 2026');
    expect(october).toHaveTextContent('400,00 €');
  });

  it('keeps the chosen period in the URL and asks for it', async () => {
    const seen: string[] = [];
    serve(QUARTER, seen);
    const { user, router, container } = await renderApp(`/income/${SALARY}`);

    await groups();
    await user.click(screen.getByRole('radio', { name: 'Année en cours' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ expected: 'year' }));
    const year = expectedRange('year', todayIso());
    await waitFor(() => expect(seen).toContain(`?from=${year.from}&to=${year.to}`));
    expect(screen.getByRole('radio', { name: 'Année en cours' })).toBeChecked();
    await user.click(screen.getByRole('radio', { name: '12 prochains mois' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('expected'));
    await expectNoAxeViolations(container);
  });

  it('starts from the period in the URL', async () => {
    const seen: string[] = [];
    serve(QUARTER, seen);
    await renderApp(`/income/${SALARY}?expected=next_year`);

    await groups();
    const next = expectedRange('next_year', todayIso());
    expect(seen).toEqual([`?from=${next.from}&to=${next.to}`]);
    expect(screen.getByRole('radio', { name: 'Année prochaine' })).toBeChecked();
  });

  it('ignores an unknown period in the URL', async () => {
    const seen: string[] = [];
    serve(QUARTER, seen);
    await renderApp(`/income/${SALARY}?expected=forever`);

    await groups();
    const twelve = expectedRange('next12', todayIso());
    expect(seen).toEqual([`?from=${twelve.from}&to=${twelve.to}`]);
    expect(screen.getByRole('radio', { name: '12 prochains mois' })).toBeChecked();
  });

  it('says when nothing is expected, without a chart', async () => {
    serve([month(2026, 10), month(2026, 11)]);
    const { container } = await renderApp(`/income/${SALARY}`);

    const region = await screen.findByRole('region', { name: 'Revenu attendu' });
    expect(await within(region).findByText('Rien n’est attendu sur cette période.')).toBeInTheDocument();
    expect(within(region).queryByRole('table')).not.toBeInTheDocument();
    expect(within(region).queryByRole('application')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows a failed read as a problem', async () => {
    serve(QUARTER);
    server.use(http.get('*/api/v1/income/sources/:id/expected', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp(`/income/${SALARY}`);

    const region = await screen.findByRole('region', { name: 'Revenu attendu' });
    expect(await within(region).findByRole('alert', {}, { timeout: 8000 })).toBeInTheDocument();
  });

  it('is shown to a reader and on an ended source', async () => {
    serve(QUARTER, [], detail({ canWrite: false, ended: true, endedOn: '2025-06-30' }));
    await renderApp(`/income/${SALARY}`);

    expect(await groups()).toHaveLength(3);
  });
});
