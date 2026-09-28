import { describe, expect, it } from 'vitest';
import { filterSubscriptions, labelParam, labelsInUse, labelsOf } from './filter';
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

const label = (key: string, name: string) => ({ key, name, color: '#123456' });
const withLabels = (displayName: string, labels: ReturnType<typeof label>[]) =>
  ({ ...row(displayName, null, '2025-11-01'), provider: { key: displayName.toLowerCase(), name: displayName, origin: 'catalog', labels, iconUrl: null } }) as Summary;

describe('label filter', () => {
  const video = label('streaming-video', 'Video');
  const vpn = label('vpn', 'VPN');
  const items = [withLabels('Netflix', [video]), withLabels('Proton', [vpn]), withLabels('Box', [video, vpn]), row('Sans fournisseur', null, null)];

  it('keeps everything when no label is on', () =>
    expect(filterSubscriptions(items, { labels: [] }, '2025-10-15')).toHaveLength(4));

  it('keeps a subscription whose provider carries one of the labels on', () => {
    expect(filterSubscriptions(items, { labels: ['vpn'] }, '2025-10-15').map((s) => s.displayName)).toEqual(['Proton', 'Box']);
    expect(filterSubscriptions(items, { labels: ['vpn', 'streaming-video'] }, '2025-10-15').map((s) => s.displayName))
      .toEqual(['Netflix', 'Proton', 'Box']);
  });

  it('drops a subscription without provider once a label is on', () =>
    expect(filterSubscriptions(items, { labels: ['vpn'] }, '2025-10-15').map((s) => s.displayName)).not.toContain('Sans fournisseur'));

  it('combines with the name and the state', () =>
    expect(filterSubscriptions(items, { labels: ['streaming-video'], q: 'box', status: 'active' }, '2025-10-15').map((s) => s.displayName))
      .toEqual(['Box']));

  it('reads and writes the comma-joined parameter, dropping blanks and duplicates', () => {
    expect(labelsOf(undefined)).toEqual([]);
    expect(labelsOf('vpn, ,vpn,streaming-video')).toEqual(['vpn', 'streaming-video']);
    expect(labelParam([])).toBeUndefined();
    expect(labelParam(['vpn', 'streaming-video'])).toBe('vpn,streaming-video');
  });

  it('offers each label in use once, sorted by name', () =>
    expect(labelsInUse(items).map((l) => l.name)).toEqual(['Video', 'VPN']));
});
