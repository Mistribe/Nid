import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { MonthChanges } from '@/api/dashboard';
import { dashboard, emptyMonthChanges, monthChanges, monthFigure } from './testData';

function serve(changes: MonthChanges, seen: string[] = []) {
  server.use(
    http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())),
    http.get('*/api/v1/dashboard/changes', ({ request }) => { seen.push(new URL(request.url).search); return HttpResponse.json(changes); }),
  );
}

describe('MonthChanges', () => {
  it('shows each figure’s change and the server’s causes', async () => {
    const seen: string[] = [];
    serve(monthChanges({
      netWorth: monthFigure('10000.00', '11680.00', '1680.00', [['investments.value', '1200.00'], ['real_estate.paid_in', '480.00']]),
      commitments: monthFigure('250.00', '251.00', '1.00', [['subscriptions', '1.00']]),
      income: monthFigure('2500.00', '2500.00', '0.00'),
    }), seen);
    const { container } = await renderApp('/dashboard');

    const block = await screen.findByRole('region', { name: 'Depuis le mois dernier' });
    expect(block).toHaveTextContent('depuis le 15 septembre 2025');
    const [worth, commitments, income] = Array.from(block.querySelector('ul')?.children ?? []);
    expect(worth).toHaveTextContent('Patrimoine net');
    expect(worth).toHaveTextContent('+1 680,00 €');
    expect(worth).toHaveTextContent('+1 200,00 € sur la valeur des placements');
    expect(worth).toHaveTextContent('+480,00 € de capital remboursé');
    expect(commitments).toHaveTextContent('+1,00 € d’abonnements');
    expect(income).toHaveTextContent('Revenus nets / mois');
    expect(income).toHaveTextContent('0,00 €');
    expect(seen).toContain('?scope=household');
    await expectNoAxeViolations(container);
  });

  it('leaves out a figure with nothing to compare and names an unknown cause « autres postes »', async () => {
    serve(monthChanges({ commitments: monthFigure('250.00', '200.00', '-50.00', [['newmodule', '-50.00']]) }));
    await renderApp('/dashboard');

    const block = await screen.findByRole('region', { name: 'Depuis le mois dernier' });
    expect(block).not.toHaveTextContent('Patrimoine net');
    expect(block).toHaveTextContent('d’autres postes');
  });

  it('is hidden in the first month', async () => {
    serve(emptyMonthChanges);
    await renderApp('/dashboard');
    await screen.findByRole('button', { name: /Patrimoine net/ });
    expect(screen.queryByRole('region', { name: 'Depuis le mois dernier' })).not.toBeInTheDocument();
  });
});
