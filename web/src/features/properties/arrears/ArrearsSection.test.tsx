import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PropertyArrears } from '../api';
import { arrears, PROPERTY, propertyDetail, rentedFigures, tenancy } from '../testData';

const open = async (view: PropertyArrears) => {
  server.use(
    http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail({ figures: rentedFigures(), tenancies: [tenancy()] }))),
    http.get('*/api/v1/realestate/properties/:id/arrears', () => HttpResponse.json(view)),
  );
  return renderApp(`/properties/${PROPERTY}?tab=rental`);
};

describe('arrears section', () => {
  beforeAll(async () => { await import('./ArrearsSection'); }, 30_000);

  it('lists the unpaid months with their figures, status and the totals as received', async () => {
    const { container } = await open(arrears());

    const table = await screen.findByRole('table', { name: 'Mois impayés' });
    const rows = within(table).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('septembre 2026');
    expect(rows[1]).toHaveTextContent('950,00 €');
    expect(rows[1]).toHaveTextContent('0,00 €');
    expect(rows[1]).toHaveTextContent('En retard');
    expect(rows[2]).toHaveTextContent('octobre 2026');
    expect(rows[2]).toHaveTextContent('En cours');
    expect(screen.getByText(/Total restant dû : 1\s900,00 €/)).toBeInTheDocument();
    expect(screen.getByText(/dont en retard : 950,00 €/)).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('offers « Courrier de relance » when a letter exists', async () => {
    await open(arrears());
    expect(await screen.findByRole('link', { name: 'Courrier de relance' })).toHaveAttribute('href', `/properties/${PROPERTY}/arrears-letter`);
  });

  it('offers no letter and no overdue total when nothing is overdue', async () => {
    await open(arrears({ letter: null, overdueTotal: '0.00', months: [{ month: '2026-10', due: '950.00', imputed: '0.00', remaining: '950.00', overdue: false, inLetter: false }] }));
    await screen.findByRole('table', { name: 'Mois impayés' });
    expect(screen.queryByRole('link', { name: 'Courrier de relance' })).not.toBeInTheDocument();
    expect(screen.queryByText(/dont en retard/)).not.toBeInTheDocument();
  });

  it('lists a changeover month once per tenant', async () => {
    await open(arrears({ months: [
      { month: '2026-06', due: '396.67', imputed: '0.00', remaining: '396.67', overdue: true, inLetter: false },
      { month: '2026-06', due: '480.00', imputed: '0.00', remaining: '480.00', overdue: true, inLetter: true },
    ] }));
    const table = await screen.findByRole('table', { name: 'Mois impayés' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((r) => r.textContent)).toEqual([expect.stringContaining('396,67'), expect.stringContaining('480,00')]);
    expect(rows.every((r) => r.textContent?.includes('juin 2026'))).toBe(true);
  });

  it('shows nothing when no month is listed', async () => {
    await open(arrears({ letter: null, months: [], total: '0.00', overdueTotal: '0.00' }));
    await screen.findByRole('heading', { name: 'Location' });
    expect(screen.queryByRole('table', { name: 'Mois impayés' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Loyers impayés' })).not.toBeInTheDocument();
  });
});
