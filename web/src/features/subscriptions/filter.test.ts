import { describe, expect, it } from 'vitest';
import { filterSubscriptions } from './filter';
import type { Summary } from './api';

const base = { provider: null, autoRenew: true, nextChargeAmount: null, cashCost: [], amortizedCost: [], cashCostConverted: null,
  amortizedCostConverted: null, convertedIsEstimate: false, findings: [] } as unknown as Omit<Summary, 'id' | 'displayName' | 'expiresOn' | 'nextChargeOn'>;
const row = (displayName: string, expiresOn: string | null, nextChargeOn: string | null) =>
  ({ ...base, id: displayName, displayName, expiresOn, nextChargeOn }) as Summary;

describe('filterSubscriptions', () => {
  const items = [row('Netflix', null, '2025-11-01'), row('Ancien VPN', '2025-01-01', null), row('Spotify', '2026-01-28', '2026-01-28')];

  it('matches the name ignoring case and accents', () =>
    expect(filterSubscriptions(items, { q: 'ancien vpn' }, '2025-10-15').map((s) => s.displayName)).toEqual(['Ancien VPN']));

  it('tells active from ended by expiry before today', () => {
    expect(filterSubscriptions(items, { status: 'ended' }, '2025-10-15').map((s) => s.displayName)).toEqual(['Ancien VPN']);
    expect(filterSubscriptions(items, { status: 'active' }, '2025-10-15').map((s) => s.displayName)).toEqual(['Netflix', 'Spotify']);
  });
});
