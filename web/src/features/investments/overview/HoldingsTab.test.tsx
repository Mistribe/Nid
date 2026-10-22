import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { HouseholdHoldings } from '../api';
import { householdHoldings, LIFE, PEA, WORLD_PEA } from '../testData';

const serve = (body: HouseholdHoldings, seen: string[] = []) =>
  server.use(http.get('*/api/v1/investments/overview/holdings', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
const region = (name: string) => screen.findByRole('region', { name });

describe('Placements › supports et frais', () => {
  beforeAll(async () => { await import('../EnvelopesPage'); await import('./HoldingsTab'); }, 30_000);

  it('is absent while no envelope is detailed', async () => {
    await renderApp('/investments?tab=holdings');

    expect(await screen.findByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByRole('tab', { name: 'Supports et frais' })).not.toBeInTheDocument();
  });

  it('shows the server figures and the holding held twice', async () => {
    const seen: string[] = [];
    serve(householdHoldings(), seen);
    const { container } = await renderApp('/investments?tab=holdings');

    const figures = (await screen.findByText('Détaillé par support')).closest('dl')!;
    expect(figures).toHaveTextContent('139 700,00 €2 enveloppes sur 3 · 85,8 % de la valeur');
    expect(figures).toHaveTextContent('Part en unités de compte64,0 %assurance-vie et PER · plancher 60,0 %');
    expect(figures).toHaveTextContent('Frais estimés en 2024≈ 233,75 €≈ 0,9 % des encours · estimation');
    expect(figures).toHaveTextContent('Prochaine échéance5 déc. 2025Athena 2023');
    expect(screen.getByRole('note')).toHaveTextContent('Amundi MSCI World pèse 43,2 % du foyer, détenu dans 2 enveloppes.');
    expect(seen[0]).toBe('?scope=household');
    await expectNoAxeViolations(container);
  });

  it('splits by kind, says what is outside, and lists the holdings to their page', async () => {
    serve(householdHoldings());
    const { user } = await renderApp('/investments?tab=holdings');

    const kinds = await region('Exposition par type de support');
    expect(within(kinds).getAllByRole('row')[0]).toHaveTextContent('ETF79 900,00 €57,2 %');
    expect(kinds).toHaveTextContent('Hors détail : Livret A 22 950,00 €, non réparti ou évolution depuis le relevé 200,00 €.');

    const table = await screen.findByRole('table', { name: 'Supports des enveloppes' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Support', 'Enveloppe', 'Valeur', 'Poids', 'Gain %', 'Rendement annuel', 'Frais totaux']);
    const [first] = within(table).getAllByRole('row').slice(1);
    expect(within(first!).getByRole('link', { name: /Amundi MSCI World/ })).toHaveAttribute('href', `/investments/${PEA}/holdings/${WORLD_PEA}`);
    await user.click(screen.getByRole('radio', { name: 'Par enveloppe' }));
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('Assurance-vie');
  });

  it('says the fees are an estimate and what to watch', async () => {
    serve(householdHoldings());
    await renderApp('/investments?tab=holdings');

    const fees = await region('Ce que coûtent les frais (2024)');
    expect(fees).toHaveTextContent('iShares MSCI World 0,9 %≈ 233,75 €');
    expect(fees).toHaveTextContent('Toujours une estimation.');
    expect(fees).toHaveTextContent('2 supports n’ont pas de rapport de frais pour 2024.');
    const watch = within(await region('À surveiller')).getAllByRole('listitem');
    expect(watch[0]).toHaveTextContent('PEAParts inexpliquées sur Amundi MSCI World au relevé du 30 sept. 2025');
    expect(within(watch[1]!).getByRole('link', { name: 'Assurance-vie' })).toHaveAttribute('href', expect.stringContaining(`/investments/${LIFE}/holdings/`));
  });
});
