import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { pick } from '@/test/controls';
import { server } from '@/test/server';
import type { ContractEnd, Settlement } from './api';
import { ARRANGEMENT_ID, detail, eur } from './testData';

const url = `/childcare/${ARRANGEMENT_ID}/end`;

const preview = (over: Partial<ContractEnd> = {}): ContractEnd => ({
  reason: 'withdrawal', notified: '2026-10-31', seniorityMonths: 13, noticeEnd: '2026-11-30', indemnityDue: true, grossRateKnown: false, ...over,
});

export const settlement = (over: Partial<Settlement> = {}): Settlement => ({
  id: ARRANGEMENT_ID, name: 'Garde de Léa', childName: 'Léa', carerName: 'Martine', start: '2025-09-01', lastDay: '2026-11-30', end: preview(), month: '2026-11',
  validated: false, problems: [], missingMonths: [],
  lines: {
    lastPay: eur('600.00'), regularisation: eur('26.37'), leaveDays: 15, leaveIndemnity: eur('300.00'), ruptureIndemnity: eur('120.00'),
    upkeep: eur('70.00'), meals: eur('50.00'), mileage: eur('0.00'), total: eur('1166.37'),
  },
  ...over,
});

function serve(writes: { path: string; body: unknown }[], end = preview()) {
  server.use(
    http.get('*/api/v1/childcare/:id', () => HttpResponse.json(detail())),
    http.get('*/api/v1/childcare/:id/end', ({ request }) => {
      const query = new URL(request.url).searchParams;
      return HttpResponse.json({ ...end, reason: query.get('reason')!, notified: query.get('notified')! });
    }),
    http.get('*/api/v1/childcare/:id/settlement', () => HttpResponse.json(settlement())),
    http.post('*/api/v1/childcare/:id/close', async ({ request }) => {
      writes.push({ path: new URL(request.url).pathname, body: await request.json() });
      return HttpResponse.json({ id: ARRANGEMENT_ID });
    }),
  );
}

beforeAll(async () => { await import('./EndContractPage'); await import('./SettlementPage'); }, 30_000);

describe('EndContractPage', () => {
  it('asks who, when and the last day, defaults it to the notice end, and asks the gross rate the indemnity needs', async () => {
    const writes: { path: string; body: unknown }[] = [];
    serve(writes);
    const { user } = await renderApp(url);
    expect(await screen.findByRole('heading', { level: 1, name: 'Mettre fin au contrat' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Continuer' }));

    await user.click(screen.getByRole('button', { name: 'Continuer' }));
    expect(await screen.findByText('Saisissez une date au format jj/mm/aaaa.', { exact: false })).toBeInTheDocument();
    await user.type(screen.getByLabelText('Lettre remise ou présentée le'), '31/10/2026');
    await user.click(screen.getByRole('button', { name: 'Continuer' }));

    expect(await screen.findByText('30 novembre 2026')).toBeInTheDocument();
    expect(screen.getByText('13 mois')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Dernier jour du contrat')).toHaveValue('30/11/2026'));
    await user.click(screen.getByRole('button', { name: 'Mettre fin au contrat' }));
    expect(await screen.findByText('Saisissez le taux horaire brut, par exemple 5,50.')).toBeInTheDocument();
    expect(writes).toEqual([]);

    await user.type(screen.getByLabelText('Taux horaire brut'), '5,50');
    await user.click(screen.getByRole('button', { name: 'Mettre fin au contrat' }));
    await waitFor(() => expect(writes).toEqual([{
      path: `/api/v1/childcare/${ARRANGEMENT_ID}/close`, body: { on: '2026-11-30', reason: 'withdrawal', notified: '2026-10-31', grossHourlyRate: '5.50' },
    }]));
    expect(await screen.findByRole('heading', { level: 1, name: 'Solde de tout compte' })).toBeInTheDocument();
  });

  it('a resignation offers the carer\'s reasons and sends no gross rate', async () => {
    const writes: { path: string; body: unknown }[] = [];
    serve(writes, preview({ indemnityDue: false }));
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('radio', { name: 'L\'assistante maternelle' }));
    expect(screen.getByRole('combobox', { name: 'Pour quelle raison ?' })).toHaveTextContent('Démission');
    await pick(user, 'Pour quelle raison ?', 'Pendant la période d\'essai');
    await pick(user, 'Pour quelle raison ?', 'Démission');
    await user.click(screen.getByRole('button', { name: 'Continuer' }));
    await user.type(screen.getByLabelText('Lettre remise ou présentée le'), '31/10/2026');
    await user.click(screen.getByRole('button', { name: 'Continuer' }));
    expect(await screen.findByText('pas due', { exact: false })).toBeInTheDocument();
    expect(screen.queryByLabelText('Taux horaire brut')).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Dernier jour du contrat')).toHaveValue('30/11/2026'));
    await user.click(screen.getByRole('button', { name: 'Mettre fin au contrat' }));
    await waitFor(() => expect(writes).toEqual([{
      path: `/api/v1/childcare/${ARRANGEMENT_ID}/close`, body: { on: '2026-11-30', reason: 'resignation', notified: '2026-10-31' },
    }]));
  });
});

describe('SettlementPage', () => {
  it('prints the server\'s lines and lists the documents to hand over', async () => {
    serve([]);
    await renderApp(`/childcare/${ARRANGEMENT_ID}/settlement`);
    const receipt = await screen.findByRole('article', { name: 'Reçu pour solde de tout compte' });
    expect(receipt).toHaveTextContent('Martine');
    expect(receipt).toHaveTextContent('Je retire mon enfant');
    expect(receipt).toHaveTextContent(/Indemnité de rupture\s*120,00/);
    expect(receipt).toHaveTextContent(/Total versé\s*1\s166,37/);
    expect(screen.getByRole('button', { name: 'Imprimer' })).toBeInTheDocument();
    expect(screen.getByText(/Pajemploi la produit/)).toBeInTheDocument();
  });

  it('waits for the last month when earlier months are not validated', async () => {
    serve([]);
    server.use(http.get('*/api/v1/childcare/:id/settlement', () => HttpResponse.json(settlement({ lines: null, missingMonths: ['2026-09'] }))));
    await renderApp(`/childcare/${ARRANGEMENT_ID}/settlement`);
    expect(await screen.findByText(/Les montants viendront avec le relevé du dernier mois/)).toBeInTheDocument();
    expect(screen.getByText(/septembre 2026/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Imprimer' })).not.toBeInTheDocument();
  });
});
