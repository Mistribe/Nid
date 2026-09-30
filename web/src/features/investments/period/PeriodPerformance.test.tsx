import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { detail, eur, PEA } from '../testData';

const period = (over = {}) => ({
  from: '2025-01-01', to: '2025-09-30', startAsOf: '2024-12-31', startValue: eur('21000.0000'),
  endAsOf: '2025-09-30', endValue: eur('25000.0000'), netContributions: eur('2000.0000'), performance: eur('2000.0000'), ...over,
});

function serve(body: object, seen: string[]) {
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail())),
    http.get('*/api/v1/investments/envelopes/:id/series', () => HttpResponse.json({ id: PEA, points: [] })),
    http.get('*/api/v1/investments/envelopes/:id/performance', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(body); }),
  );
}

describe('PeriodPerformance', () => {
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2025, 8, 30, 12)); });
  afterEach(() => vi.useRealTimers());

  it('shows the year to date by default, with both values and their dates', async () => {
    const seen: string[] = [];
    serve(period(), seen);
    const { container } = await renderApp(`/investments/${PEA}`);

    const section = await screen.findByRole('region', { name: 'Performance d’une période' });
    expect(within(section).getByRole('radio', { name: 'Depuis le début de l’année' })).toBeChecked();
    expect(await within(section).findByText(/\+2\s000,00\s€/)).toBeInTheDocument();
    expect(section).toHaveTextContent('31 décembre 2024');
    expect(seen).toContain('?from=2025-01-01&to=2025-09-30');
    await expectNoAxeViolations(container);
  });

  it('asks the previous year and the last twelve months with their dates', async () => {
    const seen: string[] = [];
    serve(period(), seen);
    const { user } = await renderApp(`/investments/${PEA}`);

    const section = await screen.findByRole('region', { name: 'Performance d’une période' });
    await user.click(within(section).getByRole('radio', { name: 'Année précédente' }));
    await user.click(within(section).getByRole('radio', { name: '12 derniers mois' }));

    await waitFor(() => expect(seen).toEqual(expect.arrayContaining(['?from=2024-01-01&to=2024-12-31', '?from=2024-09-30&to=2025-09-30'])));
  });

  it('refuses a reversed free period before sending', async () => {
    const seen: string[] = [];
    serve(period(), seen);
    const { user } = await renderApp(`/investments/${PEA}`);

    const section = await screen.findByRole('region', { name: 'Performance d’une période' });
    await user.click(within(section).getByRole('radio', { name: 'Période libre' }));
    await user.type(within(section).getByLabelText('Du'), '2025-06-30');
    await user.type(within(section).getByLabelText('Au'), '2025-01-31');
    await user.click(within(section).getByRole('button', { name: 'Calculer' }));

    expect(await within(section).findByText('La date de début doit précéder la date de fin.')).toBeInTheDocument();
    expect(seen.filter((s) => s.includes('2025-06-30'))).toEqual([]);
  });

  it('says a missing start or end value, never zero', async () => {
    const seen: string[] = [];
    serve(period({ startAsOf: null, startValue: null, netContributions: null, performance: null }), seen);
    await renderApp(`/investments/${PEA}`);

    const section = await screen.findByRole('region', { name: 'Performance d’une période' });
    expect(await within(section).findByText('Pas de valeur au début de la période.')).toBeInTheDocument();
    // A bare zero amount, not the tail of « 25 000,00 € ».
    expect(section.textContent).not.toMatch(/(?<![\d\s\u00a0\u202f])0,00\s€/);
  });

  it('asks for the period again after a value is recorded', async () => {
    const seen: string[] = [];
    serve(period(), seen);
    server.use(http.post('*/api/v1/investments/envelopes/:id/valuations', () => HttpResponse.json({ id: 'new-value' }, { status: 201 })));
    const { user } = await renderApp(`/investments/${PEA}`);

    const form = await screen.findByRole('form', { name: 'Nouvelle valeur' });
    await waitFor(() => expect(seen).toHaveLength(1));
    await user.click(within(form).getByRole('button', { name: 'Enregistrer la valeur' }));
    await within(form).findByRole('status');
    await waitFor(() => expect(seen).toHaveLength(2));
  });

  it('requests nothing for a free period until the dates are validated, then shows the result', async () => {
    const seen: string[] = [];
    serve(period(), seen);
    const { user } = await renderApp(`/investments/${PEA}`);

    const section = await screen.findByRole('region', { name: 'Performance d’une période' });
    await waitFor(() => expect(seen).toHaveLength(1));
    await user.click(within(section).getByRole('radio', { name: 'Période libre' }));
    expect(seen).toHaveLength(1);
    await user.type(within(section).getByLabelText('Du'), '2025-02-01');
    await user.type(within(section).getByLabelText('Au'), '2025-03-01');
    expect(seen).toHaveLength(1);
    await user.click(within(section).getByRole('button', { name: 'Calculer' }));
    await waitFor(() => expect(seen).toContain('?from=2025-02-01&to=2025-03-01'));
    expect(await within(section).findByText(/\+2\s000,00\s€/)).toBeInTheDocument();
  });

  it('says an empty date, and clears that message and the previous result', async () => {
    const seen: string[] = [];
    serve(period(), seen);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const section = await screen.findByRole('region', { name: 'Performance d’une période' });
    await user.click(within(section).getByRole('radio', { name: 'Période libre' }));
    await user.type(within(section).getByLabelText('Au'), '2025-03-01');
    await user.click(within(section).getByRole('button', { name: 'Calculer' }));
    expect(await within(section).findByText('Ce champ est obligatoire.')).toBeInTheDocument();
    expect(within(section).getByLabelText('Du')).toBeInvalid();
    await expectNoAxeViolations(container);
    expect(seen.filter((s) => s.includes('2025-03-01'))).toEqual([]);
  });

  it('drops the reversed-dates message and the old result when a preset is chosen again', async () => {
    const seen: string[] = [];
    serve(period(), seen);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const section = await screen.findByRole('region', { name: 'Performance d’une période' });
    await user.click(within(section).getByRole('radio', { name: 'Période libre' }));
    await user.type(within(section).getByLabelText('Du'), '2025-06-30');
    await user.type(within(section).getByLabelText('Au'), '2025-01-31');
    await user.click(within(section).getByRole('button', { name: 'Calculer' }));
    expect(await within(section).findByText('La date de début doit précéder la date de fin.')).toBeInTheDocument();
    expect(within(section).queryByText('Valeur de départ')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
    await user.click(within(section).getByRole('radio', { name: '12 derniers mois' }));
    expect(within(section).queryByText('La date de début doit précéder la date de fin.')).not.toBeInTheDocument();
  });
});
