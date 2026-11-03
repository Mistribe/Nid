import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw/http';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { resetFocus } from './focus';
import { proposal } from './testData';
import { serve } from './testServe';

const rows = [
  { dueDate: '2020-11-15', payment: '1159.92', interest: '583.33', principal: '576.59', crd: '199423.41' },
  { dueDate: '2020-12-15', payment: '1159.92', interest: '581.65', principal: '578.27', crd: '198845.14' },
  { dueDate: '2021-01-15', payment: '1159.92', interest: '579.96', principal: '579.96', crd: '198265.18' },
];
const table = (over: Parameters<typeof proposal>[0] = {}) => proposal({
  kind: 'realestate.import_amortization', targetName: 'Prêt maison', payload: { rows },
  subjectKey: '00000000-0000-7000-8000-0000000000e1', sourceLabel: 'Tableau d’amortissement', ...over,
});
const agree = { maxGap: '0.00', maxGapOn: null, divergesOn: null, interestGap: '0.00', rowsBeyondRounding: 0 };
const tablePreview = (over: object = {}) => ({
  currency: 'EUR', summary: agree, reference: { current: 'computed', proposed: 'bank' },
  first: rows, last: [], count: 3, range: { from: '2020-11-15', to: '2021-01-15' }, ...over,
});
const checkbox = { name: 'Faire de ce tableau la référence' };

describe('Un tableau d’amortissement proposé', () => {
  beforeEach(resetFocus);
  beforeAll(async () => { await import('./ProposalDetailPage'); });

  it('shows the comparison, the rows and the reference, and approves it as the reference', async () => {
    const p = table();
    const sent = serve([p], { preview: tablePreview() });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Tableau d’amortissement' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Prêt maison' })).toHaveAttribute('href', `/loans/${p.targetItemId}`);
    expect(screen.getByText(/Du 15 novembre 2020 au 15 janvier 2021/)).toBeInTheDocument();
    expect(screen.getByText('Les deux concordent (écarts d’arrondi seulement).')).toBeInTheDocument();
    expect(screen.getByText(/Référence.+Calcul/)).toBeInTheDocument();
    expect(within(screen.getByRole('table', { name: 'Lignes lues' })).getAllByRole('row')).toHaveLength(4);
    expect(screen.getByRole('checkbox', checkbox)).toBeChecked();
    expect(screen.getByRole('checkbox', checkbox)).toHaveAttribute('data-slot', 'checkbox');
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('sends makeReference false when the box is unchecked', async () => {
    const p = table();
    const sent = serve([p], { preview: tablePreview() });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('checkbox', checkbox));
    expect(screen.getByRole('checkbox', checkbox)).not.toBeChecked();
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1, editedPayload: { rows, makeReference: false } }));
  });

  it('sends nothing more when the box is unchecked then checked again', async () => {
    const p = table();
    const sent = serve([p], { preview: tablePreview() });
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('checkbox', checkbox));
    await user.click(screen.getByRole('checkbox', checkbox));
    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('tells a diverging table in French, not with the server’s sentence', async () => {
    const p = table({ warnings: [{ code: 'schedule_mismatch', severity: 'warning', message: 'The bank\'s table diverges from the calculation from 2021-01-15.', field: 'payload.rows[2]' }] });
    serve([p], { preview: tablePreview({ summary: { maxGap: '1000.00', maxGapOn: '2021-01-15', divergesOn: '2021-01-15', interestGap: '0.00', rowsBeyondRounding: 1 } }) });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText(/Le capital restant dû diverge de plus de 1,00\s€ à partir du 15 janvier 2021/)).toBeInTheDocument();
    expect(screen.queryByText(/diverges from the calculation/)).toBeNull();
    expect(screen.queryByRole('region', { name: 'Avertissements' })).toBeNull();
    await expectNoAxeViolations(container);
  });

  // Review Focus 4.
  it('has no checkbox when the bank’s table is already the reference', async () => {
    const p = table();
    const sent = serve([p], { preview: tablePreview({ reference: { current: 'bank', proposed: 'bank' } }) });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('Le tableau de la banque reste la référence de ce crédit.')).toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).toBeNull();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it.each([
    [409, { status: 409, code: 'realestate.bank_schedule_invalid', title: 'The bank table does not match the loan.' }],
    [422, { status: 422, code: 'request.invalid', title: 'Invalid.', errors: [{ path: 'payload.rows', code: 'realestate.early_repayment', message: 'Impossible.' }] }],
  ])('tells a refused approval (%i) in French, never with the server’s sentence', async (status, body) => {
    const p = table();
    serve([p], { preview: tablePreview() });
    server.use(http.post('*/api/v1/core/proposals/:id/approve', () => HttpResponse.json(body, { status })));
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    expect(await screen.findByText('Ce tableau ne correspond plus au crédit : rejetez-le et demandez-en un à jour.')).toBeInTheDocument();
    expect(screen.queryByText(/bank table|Impossible/)).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('names the first refused row, counted from 1', async () => {
    const p = table();
    serve([p], { preview: tablePreview() });
    server.use(http.post('*/api/v1/core/proposals/:id/approve', () => HttpResponse.json(
      { status: 422, code: 'realestate.bank_schedule_invalid', title: 'x', errors: [{ path: 'payload.rows[11].crd', code: 'realestate.bank_schedule_invalid', message: 'x' }] },
      { status: 422 })));
    const { user } = await renderApp(`/proposals/${p.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    expect(await screen.findByText('Ce tableau ne correspond plus au crédit (ligne 12) : rejetez-le et demandez-en un à jour.')).toBeInTheDocument();
  });

  it('marks the rows left out between the first and the last', async () => {
    const p = table();
    serve([p], { preview: tablePreview({ first: rows.slice(0, 2), last: rows.slice(2), count: 240 }) });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('… 237 échéances …')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });
});
