import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { IncomeSourceDetail } from './api';
import { detail, eur, phase, SALARY } from './testData';

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

  it('changes a component\u2019s amount from its row menu', async () => {
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
      http.get('*/api/v1/income/sources/:id/expected', ({ request, params }) => {
        const url = new URL(request.url);
        const [y, m] = [Number(url.searchParams.get('from')!.slice(0, 4)), Number(url.searchParams.get('from')!.slice(5, 7))];
        return HttpResponse.json({
          id: params.id, from: url.searchParams.get('from'), to: url.searchParams.get('to'),
          months: [{ year: y, month: m, oneOffs: [], total: eur('2300.00'), occurrences: [{ phaseId: 'p1', label: 'Salaire de base', on: `${y}-${String(m).padStart(2, '0')}-28`, amount: eur('2300.00') }] }],
        });
      }),
      http.post('*/api/v1/income/sources/:id/phase-changes', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json({ id: 'p10' }, { status: 201 }); }),
    );
    const { user, container } = await renderApp(`/income/${SALARY}`);

    await screen.findByRole('table', { name: 'Salaire de base' });
    await user.click(screen.getAllByRole('button', { name: /Actions de la période Salaire de base du 1 janvier 2025/ })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Changer le montant' }));
    const dialog = await screen.findByRole('dialog', { name: 'Changer le montant' });
    await within(dialog).findByRole('combobox', { name: 'Date d\u2019effet' });
    await user.clear(within(dialog).getByLabelText('Montant net'));
    await user.type(within(dialog).getByLabelText('Montant net'), '2500');
    await expectNoAxeViolations(dialog);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer le changement' }));

    await waitFor(() => expect(sent).toEqual([expect.objectContaining({ label: 'Salaire de base', netAmount: { amount: '2500', currency: 'EUR' } })]));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Montant chang\u00e9.'));
    await expectNoAxeViolations(container);
  });

  it('ends and reopens the source from the \u22ef menu', async () => {
    let current = detail();
    server.use(
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(current)),
      http.post('*/api/v1/income/sources/:id/close', () => { current = { ...current, ended: true, endedOn: '2025-06-30' }; return HttpResponse.json({ id: SALARY }); }),
      http.post('*/api/v1/income/sources/:id/reopen', () => { current = { ...current, ended: false, endedOn: null }; return HttpResponse.json({ id: SALARY }); }),
    );
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Autres actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Terminer cette source' }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Terminer la source' })).getByRole('button', { name: 'Terminer' }));
    expect(await screen.findByText('Source termin\u00e9e', { exact: true })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Autres actions' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Rouvrir' }));
    expect(await screen.findByRole('button', { name: 'Autres actions' })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Source rouverte.');
  });

  it('a reader sees no write action', async () => {
    serve(detail({ canWrite: false, visibility: 'shared' }));
    const { container } = await renderApp(`/income/${SALARY}`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    const article = screen.getByRole('article');
    expect(within(article).queryAllByRole('button')).toEqual([]);
    expect(within(article).queryByRole('tab', { name: 'R\u00e9glages' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });
});
