import { describe, expect, it } from 'vitest';
import i18n from '@/app/i18n';
import '@/locales/tax';
import type { TaxNotice } from '@/api/tax';
import { foldGroups, noticeGroups, type NoticeGroup } from './TaxNotices';

const t = i18n.t.bind(i18n);
const notice = (code: string, item: string, amount = '10.00', extra: Partial<TaxNotice> = {}): TaxNotice => ({ code, item, name: `n-${item}`, amount, ...extra });
const groups = (notices: TaxNotice[], warnings?: string[]) => noticeGroups(t, { notices, warnings, year: 2026, rulesYear: 2025 });
const group = (kind: string, count: number): NoticeGroup => ({
  kind, tile: '/tax', heading: kind, items: Array.from({ length: count }, (_, i) => ({ key: `${kind}${i}`, name: `${kind}${i}` })),
});

describe('noticeGroups', () => {
  it('groups notices by kind and says the shared advice once', () => {
    const g = groups([notice('savings_interest', 's1'), notice('savings_interest', 's2'), notice('savings_interest', 's3'), notice('missing_gross', 'b')]);
    expect(g.map((x) => x.kind)).toEqual(['missing_gross', 'savings_interest']);
    expect(g[1]!.items).toHaveLength(3);
    expect(g[1]!.heading).toBe(t('tax.notices.kind.savings_interest'));
  });

  it('puts warnings first, once each, without items', () => {
    const g = groups([notice('missing_gross', 'b')], ['per_over_ceiling', 'per_over_ceiling', 'rules_provisional']);
    expect(g.slice(0, 2).map((x) => x.kind)).toEqual(['rules_provisional', 'per_over_ceiling']);
    expect(g[0]!.items).toEqual([]);
    expect(g[1]!.tile).toBe('/retirement');
  });

  it('links each item to what it concerns', () => {
    const link = (code: string, id: string) => groups([notice(code, id)])[0]!.items[0]!.link;
    expect(link('savings_interest', 's')).toEqual({ to: '/investments/$id', params: { id: 's' } });
    expect(link('rental_charges_removed', 'p')).toEqual({ to: '/properties/$id', params: { id: 'p' } });
    expect(link('rental_fee_not_counted', 'l')).toEqual({ to: '/loans/$id', params: { id: 'l' } });
    expect(link('missing_gross', 'b')).toEqual({ to: '/income/$id', params: { id: 'b' } });
    expect(link('other_currency', 'o')).toBeUndefined();
  });

  it("merges one arrangement's childcare notices into one item with its summary link", () => {
    const g = groups([
      notice('childcare_from_statements', 'c1', '1728.81'),
      notice('childcare_projected', 'c1', '900.00', { monthsValidated: 7, monthsEstimated: 5 }),
      notice('childcare_estimated', 'c2', '500.00', { monthsEstimated: 3 }),
    ]);
    expect(g).toHaveLength(1);
    expect(g[0]!.items).toHaveLength(2);
    const c1 = g[0]!.items[0]!;
    const detail = c1.detail!.replace(/\s/g, ' ');
    expect(detail).toContain('1 728,81 €');
    expect(detail).toContain('900,00 €');
    expect(c1.amount).toBeUndefined();
    expect(c1.link?.search).toEqual({ tab: 'summary', year: '2026' });
  });

  it('a contribution rate reads as a percent, a notice without amount shows none', () => {
    expect(groups([notice('contribution_rate_differs', 'i', '22.85')])[0]!.items[0]!.amount).toBe('22,85 %');
    expect(groups([notice('per_without_contributions', 'e')])[0]!.items[0]!.amount).toBeUndefined();
  });

  it('an unknown code reads as itself, last', () => {
    const g = groups([notice('brand_new', 'x'), notice('missing_gross', 'b')]);
    expect(g.map((x) => x.heading)).toEqual([t('tax.notices.kind.missing_gross'), 'brand_new']);
  });
});

describe('foldGroups', () => {
  it('keeps five rows and the heading of a group cut in two', () => {
    const w = group('rules_provisional', 0);
    const s = group('savings_interest', 6);
    const f = foldGroups([w, s], 5);
    expect(f.shown).toEqual([w, { ...s, items: s.items.slice(0, 4) }]);
    expect(f.hidden).toEqual([{ ...s, items: s.items.slice(4), continued: true }]);
    expect(f.hiddenCount).toBe(2);
  });

  it('folds nothing at five rows', () => {
    const f = foldGroups([group('a', 3), group('b', 2)], 5);
    expect(f.hidden).toEqual([]);
    expect(f.hiddenCount).toBe(0);
  });
});
