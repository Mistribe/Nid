import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { NannyDetail } from './nannyApi';
import { monthsOf } from './NannyMonthDialog';

const ID = '00000000-0000-0000-0000-0000000000a1';
const eur = (amount: string) => ({ amount, currency: 'EUR' });
const detail = (over: Partial<NannyDetail> = {}): NannyDetail => ({
  summary: { id: ID, name: 'Garde à domicile', owners: [], visibility: 'shared', closed: false, writable: true, netCost: eur('1300.00'), months: 2 },
  year: 2024, years: [2025, 2024],
  months: [
    { month: '2024-02', paid: eur('1200.00'), cmg: eur('500.00'), netCost: eur('700.00'), hours: null },
    { month: '2024-01', paid: eur('1000.00'), cmg: eur('400.00'), netCost: eur('600.00'), hours: '80' },
  ],
  paid: eur('2200.00'), cmg: eur('900.00'), netCost: eur('1300.00'), projection: null,
  ...over,
});
const stat = (label: string) => screen.getByText(label, { selector: 'dt' }).parentElement as HTMLElement;

describe('NannyDetailPage', () => {
  beforeAll(async () => { await import('./NannyDetailPage'); }, 30_000);

  it('shows the year as the server sums it, and the rest of the current year when projected', async () => {
    server.use(http.get('*/api/v1/childcare/nannies/:id', () => HttpResponse.json(detail({ projection: { months: 10, mean: eur('650.00'), total: eur('7800.00') } }))));
    const { container } = await renderApp(`/childcare/nannies/${ID}`);

    expect(await screen.findByRole('heading', { name: 'Garde à domicile', level: 1 })).toBeInTheDocument();
    expect(stat('Payé')).toHaveTextContent('2 200,00 €');
    expect(stat('CMG reçue')).toHaveTextContent('900,00 €');
    expect(stat('Coût net')).toHaveTextContent('1 300,00 €');
    expect(stat('Estimation sur l’année')).toHaveTextContent('7 800,00 €');
    expect(stat('Estimation sur l’année')).toHaveTextContent('dont 10 mois restants comptés à 650,00 €');
    expect(within(screen.getByRole('table', { name: 'Mois 2024' })).getAllByRole('row')[1]).toHaveTextContent('700,00 €');
    await expectNoAxeViolations(container);
  });

  it('records a month in a dialog and deletes one after a confirmation', async () => {
    const puts: { url: string; body: unknown }[] = [];
    const deletes: string[] = [];
    server.use(
      http.get('*/api/v1/childcare/nannies/:id', () => HttpResponse.json(detail())),
      http.put('*/api/v1/childcare/nannies/:id/months/:month', async ({ request }) => { puts.push({ url: new URL(request.url).pathname, body: await request.json() }); return HttpResponse.json({ id: ID }); }),
      http.delete('*/api/v1/childcare/nannies/:id/months/:month', ({ request }) => { deletes.push(new URL(request.url).pathname); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp(`/childcare/nannies/${ID}`);

    await user.click(await screen.findByRole('button', { name: 'Enregistrer un mois' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Montant payé'), '1100,50');
    await user.type(within(dialog).getByLabelText('CMG reçue'), '450');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(puts).toEqual([{ url: `/api/v1/childcare/nannies/${ID}/months/2024-12`, body: { paid: '1100.50', cmg: '450', hours: null } }]));

    await user.click(screen.getAllByRole('button', { name: 'Actions sur janvier 2024' })[0]!);
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(deletes).toEqual([`/api/v1/childcare/nannies/${ID}/months/2024-01`]));
  });

  it('offers the months begun, newest first', () => {
    expect(monthsOf(2025, '2025-03-10')).toEqual(['2025-03', '2025-02', '2025-01']);
    expect(monthsOf(2024, '2025-03-10')).toHaveLength(12);
    expect(monthsOf(2026, '2025-03-10')).toEqual([]);
  });
});
