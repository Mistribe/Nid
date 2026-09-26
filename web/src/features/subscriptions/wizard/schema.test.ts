import { describe, expect, it } from 'vitest';
import { percentToBp, toCreateBody, wizardSchema, type WizardValues } from './schema';

const ALICE = '01926b9e-0000-7000-8000-00000000000a';
const BOB = '01926b9e-0000-7000-8000-00000000000b';
const t = ((key: string) => key) as unknown as Parameters<typeof wizardSchema>[0];

const values = (over: Partial<WizardValues> = {}): WizardValues => ({
  offer: 'recurring', trialVariant: 'free',
  provider: null, displayName: 'Netflix', visibility: 'shared', owners: [{ personId: ALICE, share: '100' }], users: [], note: '',
  startsOn: '2025-01-01', amount: '9,99', currency: 'EUR', everyCount: '1', everyUnit: 'month', sameCovers: true,
  coversCount: '1', coversUnit: 'month', maxCycles: '', payerType: 'household', payerPerson: '',
  offerDate: '2025-01-01', offerLabel: 'Offre', offerAmount: '0', offerPayerType: 'household', offerPayerPerson: '',
  paidCount: '12', paidUnit: 'month', bonusCount: '', bonusUnit: 'month', durationCount: '1', durationUnit: 'month',
  giftReceived: true, allocated: '0', launchAmount: '0,99', launchCycles: '3', nextStartsOn: '2025-04-01', others: [], then: false,
  ...over,
});

describe('percentToBp', () => {
  it.each([['100', 10000], ['50', 5000], ['33.33', 3333], ['33,34', 3334], ['0.5', 50]] as const)('reads %s %% as %i bp', (text, bp) =>
    expect(percentToBp(text)).toBe(bp));
  it.each(['', 'abc', '12.345', '101', '-5'])('refuses %j', (text) => expect(percentToBp(text)).toBeNull());
});

describe('toCreateBody', () => {
  it('writes the form as one creation request with its first phase', () =>
    expect(toCreateBody(values({ provider: { key: 'netflix', name: 'Netflix' }, users: [ALICE], note: 'n' }))).toEqual({
      displayName: 'Netflix', visibility: 'shared', owners: [{ personId: ALICE, shareBp: 10000 }], providerKey: 'netflix',
      autoRenew: true, users: [ALICE], note: 'n',
      phases: [{
        startsOn: '2025-01-01', endsOn: null, price: { amount: '9.99', currency: 'EUR' }, every: { count: 1, unit: 'month' },
        covers: null, maxCycles: null, payer: { type: 'household', personId: null },
      }],
      purchases: [],
    }));

  it('sends a covered period, a cycle limit and a paying person when given', () => {
    const body = toCreateBody(values({ sameCovers: false, coversCount: '3', coversUnit: 'month', maxCycles: '4', payerType: 'person', payerPerson: BOB }));
    expect(body.phases?.[0]).toMatchObject({ covers: { count: 3, unit: 'month' }, maxCycles: 4, payer: { type: 'person', personId: BOB } });
  });
});

describe('wizardSchema', () => {
  it('refuses shares that do not total 100 %, on the owners', () => {
    const result = wizardSchema(t).safeParse(values({ owners: [{ personId: ALICE, share: '50' }, { personId: BOB, share: '40' }] }));
    expect(result.success).toBe(false);
    expect(result.error?.issues.map((i) => i.path.join('.'))).toContain('owners');
  });

  it('refuses two owners on a private subscription', () => {
    const result = wizardSchema(t).safeParse(values({ visibility: 'private', owners: [{ personId: ALICE, share: '50' }, { personId: BOB, share: '50' }] }));
    expect(result.success).toBe(false);
  });

  it('accepts a valid recurring offer', () => expect(wizardSchema(t).safeParse(values()).success).toBe(true));
});
