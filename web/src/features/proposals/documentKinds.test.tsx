import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { resetAnnouncement } from './announce';
import { proposal } from './testData';
import { serve } from './testServe';

const payslipPayload = { paidOn: '2025-09-28', phaseLabel: 'Salaire', net: { amount: '2400.00', currency: 'EUR' }, effectiveOn: '2025-09-01' };
const payslip = () => proposal({ kind: 'income.change_pay', targetName: 'Salaire', payload: payslipPayload, subjectKey: 'Salaire:2025-09', sourceLabel: 'Bulletin septembre' });
const payslipPreview = {
  expected: { net: { amount: '2300.0000', currency: 'EUR' }, gross: null },
  proposed: { net: { amount: '2400.00', currency: 'EUR' }, gross: null },
  effectiveOn: '2025-09-01',
};
const invoicePayload = { chargedOn: '2025-09-01', amount: { amount: '10.99', currency: 'EUR' } };
const invoice = () => proposal({ kind: 'subscriptions.change_price', targetName: 'Netflix', payload: invoicePayload, subjectKey: '2025-09-01', sourceLabel: 'Facture septembre' });
const invoicePreview = {
  current: { price: { amount: '9.9900', currency: 'EUR' }, every: { count: 1, unit: 'month' } },
  proposed: { price: { amount: '10.99', currency: 'EUR' } },
  startsOn: '2025-09-01',
};
const decided = <T extends { status: string }>(p: T) => ({ ...p, status: 'approved' as const, decidedAt: '2026-10-16T09:00:00+00:00', decidedByPersonId: 'a' });

describe('Un bulletin de paie et une facture proposés', () => {
  beforeEach(resetAnnouncement);
  beforeAll(async () => { await import('./ProposalDetailPage'); });

  it('compares a payslip with the expected pay and approves it unchanged', async () => {
    const p = payslip();
    const sent = serve([p], { preview: payslipPreview });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Bulletin de paie' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Salaire' })).toHaveAttribute('href', `/income/${p.targetItemId}`);
    expect(screen.getByRole('row', { name: /Net attendu/ })).toHaveTextContent(/2\s300,00\s€/);
    expect(screen.getByRole('row', { name: /Net du bulletin/ })).toHaveTextContent(/2\s400,00\s€/);
    expect(screen.queryByRole('row', { name: /Brut/ })).toBeNull();
    expect(screen.getByRole('row', { name: /À partir du/ })).toHaveTextContent('1 septembre 2025');
    expect(screen.getByLabelText('À partir du')).toHaveValue('01/09/2025');
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('shows a dash for a missing date and the gross when there is one', async () => {
    const p = payslip();
    serve([p], { preview: { ...payslipPreview, proposed: { ...payslipPreview.proposed, gross: { amount: '3100.00', currency: 'EUR' } }, effectiveOn: null } });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('row', { name: /À partir du/ })).toHaveTextContent('—');
    expect(screen.getByRole('row', { name: /Brut attendu/ })).toHaveTextContent('—');
    expect(screen.getByRole('row', { name: /Brut du bulletin/ })).toHaveTextContent(/3\s100,00\s€/);
  });

  it('sends an edited net and a gross', async () => {
    const p = payslip();
    const sent = serve([p], { preview: payslipPreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    const net = await screen.findByLabelText('Net (EUR)');
    await user.clear(net);
    await user.type(net, '2410');
    await user.type(screen.getByLabelText('Brut (EUR, facultatif)'), '3100,50');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: {
      paidOn: '2025-09-28', phaseLabel: 'Salaire', net: { amount: '2410', currency: 'EUR' },
      gross: { amount: '3100.50', currency: 'EUR' }, effectiveOn: '2025-09-01',
    } }));
  });

  it('prefills the gross of the payload and refuses a zero net', async () => {
    const p = proposal({ ...payslip(), payload: { ...payslipPayload, gross: { amount: '3100.0000', currency: 'EUR' } } });
    const sent = serve([p], { preview: payslipPreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByLabelText('Brut (EUR, facultatif)')).toHaveValue('3100');
    const net = screen.getByLabelText('Net (EUR)');
    await user.clear(net);
    await user.type(net, '0');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    expect(await screen.findByText('Saisissez un montant positif comme 1234,56.')).toBeInTheDocument();
    expect(sent.approve).toBeUndefined();
  });

  it('puts a refused date at the date field', async () => {
    const p = payslip();
    serve([p], { preview: payslipPreview });
    server.use(http.post('*/api/v1/core/proposals/:id/approve', () => HttpResponse.json(
      { status: 422, code: 'income.invalid_phase', title: 'x', errors: [{ path: 'payload.effectiveOn', code: 'income.invalid_phase', message: 'x' }] },
      { status: 422 })));
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(screen.getByLabelText('À partir du')).toHaveAttribute('aria-invalid', 'true'));
    expect(screen.getByText('Le nouveau salaire doit prendre effet à une date de paie de la phase en cours, après son début.')).toBeInTheDocument();
  });

  it('compares an invoice with the current price and sends an edited price', async () => {
    const p = invoice();
    const sent = serve([p], { preview: invoicePreview });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Facture d’un abonnement' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Netflix' })).toHaveAttribute('href', `/subscriptions/${p.targetItemId}`);
    expect(screen.getByRole('row', { name: /Prix actuel/ })).toHaveTextContent(/9,99\s€/);
    expect(screen.getByRole('row', { name: /Rythme actuel/ })).toHaveTextContent('1 mois');
    expect(screen.getByRole('row', { name: /Prix de la facture/ })).toHaveTextContent(/10,99\s€/);
    await expectNoAxeViolations(container);

    const price = screen.getByLabelText('Prix (EUR)');
    await user.clear(price);
    await user.type(price, '11,49');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { chargedOn: '2025-09-01', amount: { amount: '11.49', currency: 'EUR' } } }));
  });

  it('keeps the payload gross when only the net is edited', async () => {
    const gross = { amount: '3100.00', currency: 'EUR' };
    const p = proposal({ ...payslip(), payload: { ...payslipPayload, gross } });
    const sent = serve([p], { preview: payslipPreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    const net = await screen.findByLabelText('Net (EUR)');
    await user.clear(net);
    await user.type(net, '2410');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: {
      paidOn: '2025-09-28', phaseLabel: 'Salaire', net: { amount: '2410', currency: 'EUR' }, gross: { amount: '3100', currency: 'EUR' }, effectiveOn: '2025-09-01',
    } }));
  });

  it('sends an edited invoice start date', async () => {
    const p = invoice();
    const sent = serve([p], { preview: invoicePreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.clear(await screen.findByLabelText('À partir du'));
    await user.type(screen.getByLabelText('À partir du'), '2025-10-01');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { chargedOn: '2025-10-01', amount: { amount: '10.99', currency: 'EUR' } } }));
  });

  it('says a refused invoice date must come after the current phase starts', async () => {
    const p = invoice();
    serve([p], { preview: invoicePreview });
    server.use(http.post('*/api/v1/core/proposals/:id/approve', () => HttpResponse.json(
      { status: 422, code: 'subscriptions.phase_range', title: 'x', errors: [{ path: 'payload.chargedOn', code: 'subscriptions.phase_range', message: 'x' }] },
      { status: 422 })));
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));

    expect(await screen.findByText('Le nouveau prix doit commencer après le début de la phase en cours.')).toBeInTheDocument();
    expect(screen.getByLabelText('À partir du')).toHaveAttribute('aria-invalid', 'true');
    await expectNoAxeViolations(container);
  });

  it('sends no edit when a change is reverted', async () => {
    const p = invoice();
    const sent = serve([p], { preview: invoicePreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    const price = await screen.findByLabelText('Prix (EUR)');
    await user.clear(price);
    await user.type(price, '12');
    await user.clear(price);
    await user.type(price, '10,99');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));

    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('accepts a free subscription and refuses a negative price', async () => {
    const p = invoice();
    const sent = serve([p], { preview: invoicePreview });
    const { user } = await renderApp(`/proposals/${p.id}`);

    const price = await screen.findByLabelText('Prix (EUR)');
    await user.clear(price);
    await user.type(price, '-1');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    expect(await screen.findByText('Saisissez un montant de 0 ou plus, comme 10,99.')).toBeInTheDocument();
    expect(sent.approve).toBeUndefined();

    await user.clear(price);
    await user.type(price, '0');
    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { chargedOn: '2025-09-01', amount: { amount: '0', currency: 'EUR' } } }));
  });

  it.each([
    ['a payslip', payslip, payslipPreview],
    ['an invoice', invoice, invoicePreview],
  ])('shows %s decided without a form, and read only', async (_name, make, preview) => {
    const p = decided(make());
    serve([p], { preview });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('link', { name: 'Voir l’élément' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approuver' })).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('shows an invoice read only to someone who cannot decide', async () => {
    const p = invoice();
    serve([p], { preview: invoicePreview, canDecide: false });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('Seul un adulte qui peut modifier cet élément peut décider.')).toBeInTheDocument();
    expect(screen.queryByLabelText('Prix (EUR)')).toBeNull();
    await expectNoAxeViolations(container);
  });
});
