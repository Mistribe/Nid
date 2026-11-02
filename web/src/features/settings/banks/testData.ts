import { http, HttpResponse } from 'msw/http';
import { server } from '@/test/server';
import type { BankAccount, BankRule, Connection } from './api';

export const CONNECTION = '00000000-0000-7000-8000-0000000000b1';
export const CHECKING = '00000000-0000-7000-8000-0000000000b2';
export const CARD = '00000000-0000-7000-8000-0000000000b3';
export const JOINT = '00000000-0000-7000-8000-0000000000e1';

export const account = (over: Partial<BankAccount> = {}): BankAccount => ({
  id: CHECKING, name: 'Compte courant', ibanMasked: 'FR76 •••• 1234', kind: 'checking', currency: 'EUR', envelopeId: null,
  cardOfAccountId: null, autoApplyBalance: false, ignored: false, balanceConflictOn: null, lastBalance: '1520.30', lastBalanceOn: '2026-09-30',
  synced: true, canWrite: true, ...over,
});

export const rule = (over: Partial<BankRule> = {}): BankRule => ({
  id: '00000000-0000-7000-8000-0000000000c1', labelStem: 'PRLV SEPA NETFLIX', accountName: 'Compte courant', module: 'subscriptions',
  itemId: '00000000-0000-7000-8000-0000000000d1', itemName: 'Netflix', createdAt: '2026-09-02T08:00:00+00:00', lastUsedAt: '2026-10-02T04:30:00+00:00',
  ...over,
});

export const connection = (over: Partial<Connection> = {}): Connection => ({
  id: CONNECTION, aspspName: 'BNP Paribas', status: 'active', validUntil: '2027-04-27T10:00:00+00:00', lastSyncedAt: '2026-10-28T04:30:00+00:00',
  lastErrorCode: null, accounts: [account(), account({ id: CARD, name: 'Visa Premier', ibanMasked: null, kind: 'card', lastBalance: null, lastBalanceOn: null })],
  ...over,
});

/** Banking on and configured, with these connections and envelopes; `accounts` are every account the person reads (theirs by default). */
export function serveBanks(connections: Connection[], envelopes: unknown[] = [], accounts: BankAccount[] = connections.flatMap((c) => c.accounts)) {
  server.use(
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: true })),
    http.get('*/api/v1/banking/connections', () => HttpResponse.json(connections)),
    http.get('*/api/v1/banking/accounts', () => HttpResponse.json(accounts)),
    http.get('*/api/v1/banking/aspsps', () => HttpResponse.json([{ name: 'BNP Paribas', logo: null }, { name: 'Crédit Agricole', logo: null }])),
    http.get('*/api/v1/investments/envelopes', () => HttpResponse.json({ items: envelopes })),
  );
}
