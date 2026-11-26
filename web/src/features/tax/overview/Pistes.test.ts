import { describe, expect, it } from 'vitest';
import i18n from '@/app/i18n';
import '@/locales/tax';
import type { TaxResult } from '@/api/tax';
import { estimate as base, result } from '../taxTestData';
import { pistesOf } from './Pistes';

const t = i18n.t.bind(i18n);
const plain = (s?: string) => s?.replace(/[\u00a0\u202f]/g, ' ');
const of = (over: Partial<TaxResult> = {}, year = 2026) => {
  const e = base();
  return pistesOf(t, { ...e, year, result: result(over) }, 2026).map((p) => ({ ...p, title: plain(p.title)!, detail: plain(p.detail) }));
};
const per = (over: object = {}) => ({ minimum: '0.00', expiring: '0.00', excess: '0.00', contributions: '0.00', ceiling: '0.00', source: 'notice', ...over }) as never;
const alice = (p: object) => [{ ...result().declarants[0]!, per: per(p) }];
const bank = { pooled: false, remaining: '2400.00', taxSaved: '0.00', roomInBracket: '0.00', remainingTaxSaved: '720.00' };

describe('pistesOf', () => {
  it('nothing to suggest gives no piste', () => expect(of()).toEqual([]));

  it('offers the PER left this year with the tax it saves', () => {
    const p = of({ per: bank })[0]!;
    expect([p.key, p.title, p.saving]).toEqual(['per', 'Verser 2 400 € sur le PER avant le 31 décembre', '720.00']);
  });

  it("names each declarant's minimum not to lose the expiring ceiling", () => {
    expect(of({ per: bank, declarants: alice({ minimum: '500.00', expiring: '800.00' }) })[0]!.detail)
      .toBe('Alice : au moins 500 € pour ne pas perdre 800 €');
  });

  it('offers no PER when it pays nothing and loses nothing, or when nothing is left', () => {
    expect(of({ per: { ...bank, remainingTaxSaved: '0.00' } })).toEqual([]);
    expect(of({ per: { ...bank, remaining: '0.00' } })).toEqual([]);
    const kept = of({ per: { ...bank, remainingTaxSaved: '0.00' }, declarants: alice({ minimum: '500.00', expiring: '800.00' }) })[0]!;
    expect([kept.key, kept.saving]).toEqual(['per', undefined]);
  });

  it('names only the declarants who have a minimum', () => {
    const d = result().declarants[0]!;
    const two = [{ ...d, per: per({ minimum: '500.00', expiring: '800.00' }) }, { ...d, name: 'Bob', per: per() }];
    expect(of({ per: bank, declarants: two })[0]!.detail).toBe('Alice : au moins 500 € pour ne pas perdre 800 €');
  });

  it('offers neither the PER nor the cut for a past year', () => {
    const keys = of({ per: bank, withholding: { ...result().withholding, cut: { rate: '3.2', less: '600.00', declarants: [], rentalMonthly: '0.00', annual: '0.00', balance: '0.00', months: 12 } } }, 2025).map((p) => p.key);
    expect(keys).not.toContain('per');
    expect(keys).not.toContain('cut');
  });

  it('the other regime costing less is a saving, costing more is nothing to change', () => {
    const [a] = of({ rentalRegimeGap: '-344.00' });
    expect([a!.title, a!.saving]).toEqual(['Passer au réel', '344.00']);
    const [b] = of({ rentalRegimeGap: '120.00' });
    expect([b!.title, b!.same, b!.saving]).toEqual(['Rester au micro-foncier', true, undefined]);
    expect(of({ rentalRegimeGap: '0.00' })[0]!.same).toBe(true);
  });

  it("the capital option, and next year's CSG under the scale", () => {
    const a = of({ capitalOptionGap: '-120.00' })[0]!;
    expect([a.title, a.saving]).toEqual(['Opter pour le barème (case 2OP)', '120.00']);
    for (const gap of ['0.00', '-0.00']) expect([of({ capitalOptionGap: gap })[0]!.title, of({ capitalOptionGap: gap })[0]!.same]).toEqual(['Rester au prélèvement forfaitaire', true]);
    const b = of({ capitalScale: true, capitalOptionGap: '80.00', deductibleCsgNextYear: '272.00' })[0]!;
    expect(b.same).toBe(true);
    expect(b.detail).toContain('272,00 €');
  });

  it('the safe cut this year opens Prélèvement', () => {
    const p = of({ withholding: { ...result().withholding, cut: { rate: '3.2', less: '600.00', declarants: [], rentalMonthly: '0.00', annual: '0.00', balance: '0.00', months: 12 } } })[0]!;
    expect(p.key).toBe('cut');
    expect(p.title).toContain('600 €');
    // #717: asked too late for this year, no saving this year.
    const late = of({ withholding: { ...result().withholding, cut: { rate: '3.2', less: '0.00', declarants: [], rentalMonthly: '0.00', annual: '0.00', balance: '0.00', months: 0 } } });
    expect(late.map((x) => x.key)).not.toContain('cut');
  });

  it('a saving above the tax says the refund it turns into (#726)', () => {
    const per = of({ per: { pooled: false, remaining: '5000.00', taxSaved: '0.00', roomInBracket: '0.00', remainingTaxSaved: '1100.00' }, tipRefunds: { per: '800.00', rentalRegime: null, capitalOption: null } })[0]!;
    expect([per.key, per.detail?.replace(/\s/g, ' ')]).toEqual(['per', 'votre impôt deviendrait un remboursement de 800 €']);
    const plain = of({ per: { pooled: false, remaining: '5000.00', taxSaved: '0.00', roomInBracket: '0.00', remainingTaxSaved: '100.00' } })[0]!;
    expect(plain.detail).toBeUndefined();
  });

  it("an estimated ceiling asks for the notice's", () => {
    const p = of({ warnings: ['per_ceiling_estimated'], declarants: alice({ source: 'estimated' }) })[0]!;
    expect([p.key, p.link.hash, p.detail]).toEqual(['ceiling', 'retraite', 'Plafond estimé pour Alice']);
  });
});
