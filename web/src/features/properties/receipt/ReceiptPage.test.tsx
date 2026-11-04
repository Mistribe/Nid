import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { delay } from 'msw';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { expectLabelBefore } from '@/test/filters';
import { renderApp } from '@/test/renderApp';
import { expectTrailBack } from '@/test/trail';
import { server } from '@/test/server';
import { PROPERTY, propertyDetail } from '../testData';

type View = components['schemas']['RentReceiptView'];
const view = (over: Partial<View> = {}): View => ({
  property: PROPERTY, propertyName: 'Maison de Nantes', currency: 'EUR', month: '2026-09', kind: 'quittance',
  rent: '900.00', charges: '50.00', total: '950.00', paid: '950.00', paidOn: '2026-09-05',
  postalAddress: '12 rue des Lilas, 44000 Nantes', tenantNames: 'Jeanne Martin', landlordName: 'Paul Durand',
  landlordAddress: '3 quai Duguay, 44000 Nantes', missing: [], months: ['2026-09', '2026-08'], ...over,
});
const serve = (v: View) => {
  const asked: (string | null)[] = [];
  server.use(http.get('*/api/v1/realestate/properties/:id/receipts', ({ request }) => {
    const month = new URL(request.url).searchParams.get('month');
    asked.push(month);
    return HttpResponse.json(month ? { ...v, month } : v);
  }));
  return asked;
};
const PAGE = `/properties/${PROPERTY}/receipt`;

describe('receipt page', () => {
  beforeAll(async () => { await import('./ReceiptPage'); }, 30_000);

  afterEach(() => { vi.restoreAllMocks(); });

  it('loading and error states show no inline back link: the bar leads back to the property, « Bien » until it is read', async () => {
    server.use(http.get('*/api/v1/realestate/properties/:id/receipts', async () => { await delay('infinite'); return HttpResponse.json({}); }));
    await renderApp(PAGE);
    expect(await screen.findByLabelText('Chargement de la quittance')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Bien', `/properties/${PROPERTY}`));
    expectTrailBack('Biens', '/properties');
    cleanup();

    // Coming from the property, its name is already in the cache: no request names it.
    server.use(
      http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail())),
      http.get('*/api/v1/realestate/properties/:id/receipts', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })),
    );
    const { router } = await renderApp(`/properties/${PROPERTY}`);
    await screen.findByRole('heading', { level: 1, name: 'Maison de Nantes' });
    await router.navigate({ to: '/properties/$id/receipt', params: { id: PROPERTY } });
    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Maison de Nantes', `/properties/${PROPERTY}`));
    expectTrailBack('Biens', '/properties');
  });

  it('shows the quittance: parties, address, period, figures, payment date and the statement', async () => {
    serve(view());
    const { container } = await renderApp(`${PAGE}?month=2026-09`);

    const doc = await screen.findByRole('article', { name: 'Quittance de loyer' });
    expect(within(doc).getByRole('heading', { name: 'Quittance de loyer' })).toBeInTheDocument();
    expect(doc).toHaveTextContent('Paul Durand');
    expect(doc).toHaveTextContent('Jeanne Martin');
    expect(doc).toHaveTextContent('12 rue des Lilas, 44000 Nantes');
    expect(doc).toHaveTextContent('mois de septembre 2026');
    expect(doc).toHaveTextContent('900,00 €');
    expect(doc).toHaveTextContent('50,00 €');
    expect(doc).toHaveTextContent('950,00 €');
    expect(doc).toHaveTextContent('Payé le 5 septembre 2026');
    expect(doc).toHaveTextContent('déclare avoir reçu de Jeanne Martin la somme de 950,00 € au titre du loyer et des charges du mois de septembre 2026');
    expect(doc).toHaveTextContent(/Fait le /);
    await expectNoAxeViolations(container);
  });

  it('shows a reçu with its mention and the amount paid', async () => {
    serve(view({ kind: 'receipt', paid: '400.00' }));
    await renderApp(`${PAGE}?month=2026-09`);

    const doc = await screen.findByRole('article', { name: 'Reçu de paiement' });
    expect(doc).toHaveTextContent('Reçu pour solde partiel, ne vaut pas quittance');
    expect(doc).toHaveTextContent('la somme de 400,00 €, en paiement partiel');
    expect(screen.queryByText('Quittance de loyer')).not.toBeInTheDocument();
    // The table holds what is due; what was paid stands apart.
    const due = within(doc).getByLabelText('Montants dus');
    expect(due).toHaveTextContent('950,00 €');
    expect(due).not.toHaveTextContent('Payé');
    const paid = within(doc).getByText('Payé', { selector: 'dt' }).closest('dl');
    expect(paid).not.toBe(due);
    expect(paid).toHaveTextContent('400,00 €');
  });

  it('labels no table « Montants dus » on a quittance', async () => {
    serve(view());
    await renderApp(`${PAGE}?month=2026-09`);

    await screen.findByRole('article', { name: 'Quittance de loyer' });
    expect(screen.queryByText('Montants dus')).not.toBeInTheDocument();
    expect(screen.queryByText('Payé', { selector: 'dt' })).not.toBeInTheDocument();
  });

  it('falls back to the default month when a kept link asks for a month no longer offered', async () => {
    const asked: (string | null)[] = [];
    server.use(http.get('*/api/v1/realestate/properties/:id/receipts', ({ request }) => {
      const month = new URL(request.url).searchParams.get('month');
      asked.push(month);
      return month
        ? HttpResponse.json({ status: 422, code: 'realestate.receipt_month', title: 'x', errors: [{ path: 'month', code: 'realestate.receipt_month', message: 'x' }] }, { status: 422 })
        : HttpResponse.json(view());
    }));
    const { router } = await renderApp(`${PAGE}?month=2020-01`);

    expect(await screen.findByRole('article', { name: 'Quittance de loyer' })).toBeInTheDocument();
    expect(router.state.location.search).not.toHaveProperty('month');
    expect(asked).toEqual(['2020-01', null]);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows the refusal of a month for another reason', async () => {
    server.use(http.get('*/api/v1/realestate/properties/:id/receipts', () => HttpResponse.json(
      { status: 422, code: 'realestate.receipt_two_leases', title: 'Two leases cover this month.' }, { status: 422 })));
    const { router } = await renderApp(`${PAGE}?month=2026-06`);

    expect(await screen.findByText('Two leases cover this month.', { exact: false })).toBeInTheDocument();
    expect(router.state.location.search).toMatchObject({ month: '2026-06' });
  });

  it('shows no document for an unpaid month, and keeps the month selector', async () => {
    serve(view({ kind: 'unpaid', paid: '0.00', paidOn: null }));
    await renderApp(`${PAGE}?month=2026-09`);

    expect(await screen.findByText('Aucun paiement imputé à ce mois')).toBeInTheDocument();
    expect(screen.queryByRole('article', { name: 'Quittance de loyer' })).not.toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Mois' })).toBeInTheDocument();
  });

  it('warns about missing identity, links to the lease and the settings, and marks the gaps in the document', async () => {
    serve(view({ tenantNames: null, postalAddress: null, missing: ['postal_address', 'tenant_names'] }));
    await renderApp(`${PAGE}?month=2026-09`);

    const doc = await screen.findByRole('article', { name: 'Quittance de loyer' });
    expect(doc).toHaveTextContent('[à compléter]');
    const warning = screen.getByRole('alert');
    expect(warning).toHaveTextContent('adresse du logement');
    expect(warning).toHaveTextContent('nom du locataire');
    expect(within(warning).getByRole('link', { name: 'Compléter le bail' })).toHaveAttribute('href', `/properties/${PROPERTY}?tab=rental`);
    expect(within(warning).getByRole('link', { name: 'Compléter le bien' })).toHaveAttribute('href', `/properties/${PROPERTY}?tab=settings`);
  });

  it('puts the month in the filter row, the label before it, under a trail through the properties', async () => {
    serve(view());
    await renderApp(PAGE);

    const month = await screen.findByRole('combobox', { name: 'Mois' });
    expectLabelBefore(within(screen.getByRole('group', { name: 'Filtres' })).getByText('Mois'), month);
    const trail = screen.getByRole('navigation', { name: 'Fil d’Ariane' });
    expect(within(trail).getByRole('link', { name: 'Biens' })).toHaveAttribute('href', '/properties');
    expect(within(trail).getByRole('link', { name: view().propertyName })).toHaveAttribute('href', `/properties/${PROPERTY}`);
  });

  it('asks for the month picked, in French, from the months offered', async () => {
    const asked = serve(view());
    const { user, router } = await renderApp(PAGE);
    await screen.findByRole('article', { name: 'Quittance de loyer' });

    await pick(user, 'Mois', 'août 2026');

    await waitFor(() => expect(router.state.location.search).toMatchObject({ month: '2026-08' }));
    await waitFor(() => expect(asked).toContain('2026-08'));
  });

  it('prints from the toolbar, which stays out of the print', async () => {
    serve(view());
    const print = vi.spyOn(window, 'print').mockImplementation(() => undefined);
    const { user } = await renderApp(PAGE);

    const button = await screen.findByRole('button', { name: 'Imprimer' });
    expect(button.closest('.print\\:hidden')).not.toBeNull();
    expect(screen.getByRole('combobox', { name: 'Mois' }).closest('.print\\:hidden')).not.toBeNull();
    await user.click(button);
    expect(print).toHaveBeenCalledOnce();
  });
});
