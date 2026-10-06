import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { IncomeSourceDetail } from './api';
import { detail, phase, SALARY } from './testData';

const serve = (body: IncomeSourceDetail) => server.use(http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(body)));
const rowsOf = (label: string) => within(screen.getByRole('table', { name: label })).getAllByRole('row').slice(1);

describe('IncomeSourceDetailPage', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('./IncomeSourceDetailPage'); }, 30_000);

  it('shows the header, the monthly equivalent smoothed over the year, and each running component’s part', async () => {
    serve(detail());
    const { container } = await renderApp(`/income/${SALARY}`);

    const title = await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    // The settings form also offers « Salaire » and « Employeur »: the header's own texts are looked up in the header.
    const header = within(title.parentElement as HTMLElement);
    expect(header.getByText('Salaire')).toBeInTheDocument();
    expect(header.getByText('Employeur')).toBeInTheDocument();
    expect(header.getByText('Privé')).toBeInTheDocument();
    const figures = screen.getByRole('region', { name: 'Montant mensuel' });
    expect(figures).toHaveTextContent('2 450,00 € par mois');
    expect(figures).toHaveTextContent('lissé sur l’année');
    const parts = within(within(figures).getByRole('list', { name: 'Composantes en cours' })).getAllByRole('listitem');
    expect(parts.map((p) => p.textContent?.replace(/\s/g, ' '))).toEqual(['Salaire de base — 2 300,00 € par mois', 'Prime annuelle — 150,00 € par mois']);
    await expectNoAxeViolations(container);
  });

  it('shows one table per label, in French order, running then upcoming then past, newest first, statuses in words', async () => {
    serve(detail({ phases: [...detail().phases, phase({ id: 'p3', startsOn: '2027-01-01', status: 'upcoming', netAmount: { amount: '2500.00', currency: 'EUR' } })] }));
    await renderApp(`/income/${SALARY}`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    const tables = within(screen.getByRole('region', { name: 'Périodes' })).getAllByRole('table');
    expect(tables.map((table) => table.querySelector('caption')?.textContent)).toEqual(['Prime annuelle', 'Salaire de base']);
    const base = rowsOf('Salaire de base');
    expect(base.map((r) => within(r).getAllByRole('cell')[5]?.textContent)).toEqual(['En cours', 'À venir', 'Passée']);
    expect(base[0]).toHaveTextContent('1 janvier 2025');
    expect(base[0]).toHaveTextContent('sans fin');
    expect(base[0]).toHaveTextContent('2 300,00 €');
    expect(base[0]).toHaveTextContent('Chaque mois');
    expect(base[2]).toHaveTextContent('1 janvier 2024');
    expect(rowsOf('Prime annuelle')[0]).toHaveTextContent('Chaque année');
  });

  it('says how a phase capped by its occurrences ends', async () => {
    serve(detail({ phases: [phase({ maxCycles: 12 })] }));
    await renderApp(`/income/${SALARY}`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    expect(rowsOf('Salaire de base')[0]).toHaveTextContent('après 12 versements');
  });

  it('warns of a component ending with nothing after it, with its date', async () => {
    serve(detail({ gapSoon: [{ label: 'Salaire de base', endsOn: '2026-12-31' }] }));
    const { container } = await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Fin de « Salaire de base » le 31 décembre 2026 sans période suivante')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('tells the other adult the page is read-only', async () => {
    serve(detail({ canWrite: false, visibility: 'shared' }));
    const { container } = await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Vous pouvez consulter ce revenu, pas le modifier.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows an ended source with its banner and end date', async () => {
    serve(detail({ ended: true, endedOn: '2025-06-30' }));
    const { container } = await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Source terminée')).toBeInTheDocument();
    expect(screen.getByText('Terminée le 30 juin 2025')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says a source without any phase has none yet', async () => {
    serve(detail({ phases: [], monthlyEquivalent: { amount: '0.00', currency: 'EUR' } }));
    await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Aucune période pour l’instant.')).toBeInTheDocument();
    expect(screen.getByText('Aucune composante en cours.')).toBeInTheDocument();
  });

  it('says an unknown source is not found', async () => {
    server.use(http.get('*/api/v1/income/sources/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/income/${SALARY}`);

    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
  });
});
