import { describe, expect, it } from 'vitest';
import { addDuration, offerBody, stepOfPath, stepsFor, thenMode } from './offers';
import { fieldOf, type WizardValues } from './schema';

const base: WizardValues = {
  offer: 'recurring', trialVariant: 'free',
  provider: null, displayName: 'Netflix', visibility: 'shared', owners: [{ personId: 'p1', share: '100' }], users: [], note: '',
  startsOn: '2025-01-01', amount: '9,99', currency: 'EUR', everyCount: '1', everyUnit: 'month', sameCovers: true,
  coversCount: '1', coversUnit: 'month', maxCycles: '', payerType: 'household', payerPerson: '',
  offerDate: '2025-01-01', offerLabel: 'Offre', offerAmount: '0', offerPayerType: 'household', offerPayerPerson: '',
  paidCount: '12', paidUnit: 'month', bonusCount: '', bonusUnit: 'month', durationCount: '1', durationUnit: 'month',
  giftReceived: true, allocated: '0', launchAmount: '0,99', launchCycles: '3', nextStartsOn: '2025-04-01', others: [], then: false,
};
const v = (change: Partial<WizardValues>): WizardValues => ({ ...base, ...change });
const phase = { startsOn: '2025-01-01', endsOn: null, price: { amount: '9.99', currency: 'EUR' }, every: { count: 1, unit: 'month' },
  covers: null, maxCycles: null, payer: { type: 'household', personId: null } };

describe('addDuration', () => {
  it('adds days, weeks, months and years like NodaTime, the day clamped to the month', () => {
    expect(addDuration('2025-01-31', 1, 'month')).toBe('2025-02-28');
    expect(addDuration('2024-01-31', 1, 'month')).toBe('2024-02-29');
    expect(addDuration('2025-01-31', 3, 'month')).toBe('2025-04-30');
    expect(addDuration('2024-02-29', 1, 'year')).toBe('2025-02-28');
    expect(addDuration('2025-12-30', 2, 'week')).toBe('2026-01-13');
    expect(addDuration('2025-02-27', 3, 'day')).toBe('2025-03-02');
  });
});

describe('offerBody', () => {
  it('sends the recurring offer as one phase, as in 1E2a', () => {
    expect(offerBody(base)).toEqual({ phases: [phase], purchases: [] });
  });

  it('sends a prepaid term as paid then bonus grants, then the following phase when asked', () => {
    const body = offerBody(v({ offer: 'prepaid', offerAmount: '179,86', paidCount: '24', bonusCount: '3', then: true }));
    expect(body.purchases).toEqual([{
      date: '2025-01-01', label: 'Offre', kind: 'prepaid_term', price: { amount: '179.86', currency: 'EUR' },
      payer: { type: 'household', personId: null }, allocatedAmount: null, note: null,
      grants: [
        { subscriptionId: null, duration: { count: 24, unit: 'month' }, source: 'paid', startsOn: null },
        { subscriptionId: null, duration: { count: 3, unit: 'month' }, source: 'bonus', startsOn: null },
      ],
    }]);
    expect(body.phases).toEqual([phase]);
  });

  it('sends no bonus grant when the free months are left empty, and no phase without "then"', () => {
    const body = offerBody(v({ offer: 'prepaid', bonusCount: '', then: false }));
    expect(body.purchases[0]?.grants).toHaveLength(1);
    expect(body.phases).toEqual([]);
  });

  it('sends a free trial as a zero purchase with a trial grant starting on its date', () => {
    const body = offerBody(v({ offer: 'trial', trialVariant: 'free', durationCount: '1', then: true }));
    expect(body.purchases[0]).toMatchObject({
      kind: 'prepaid_term', price: { amount: '0', currency: 'EUR' }, payer: { type: 'household', personId: null },
      grants: [{ subscriptionId: null, duration: { count: 1, unit: 'month' }, source: 'trial', startsOn: '2025-01-01' }],
    });
    expect(body.phases).toEqual([phase]);
  });

  it('sends a launch price as a reduced phase ending where the normal one starts', () => {
    const body = offerBody(v({ offer: 'trial', trialVariant: 'launch', offerDate: '2025-01-31', launchAmount: '0,99',
      launchCycles: '1', nextStartsOn: '2025-02-28', amount: '9,99' }));
    expect(body.purchases).toEqual([]);
    expect(body.phases).toEqual([
      { ...phase, startsOn: '2025-01-31', endsOn: '2025-02-28', price: { amount: '0.99', currency: 'EUR' }, maxCycles: 1 },
      { ...phase, startsOn: '2025-02-28' },
    ]);
  });

  it('sends a received gift card at zero from outside the household, a bought one at its price and payer', () => {
    expect(offerBody(v({ offer: 'gift', giftReceived: true, offerAmount: '30', durationCount: '3' })).purchases[0]).toMatchObject({
      kind: 'gift', price: { amount: '0', currency: 'EUR' }, payer: { type: 'external', personId: null },
      grants: [{ subscriptionId: null, duration: { count: 3, unit: 'month' }, source: 'gift', startsOn: null }],
    });
    expect(offerBody(v({ offer: 'gift', giftReceived: false, offerAmount: '25', offerPayerType: 'person', offerPayerPerson: 'p1' }))
      .purchases[0]).toMatchObject({ price: { amount: '25', currency: 'EUR' }, payer: { type: 'person', personId: 'p1' } });
  });

  it('sends a bundle with its allocated part, its grant here and one grant per other subscription', () => {
    const body = offerBody(v({ offer: 'bundle', offerAmount: '88,99', allocated: '20', durationCount: '3',
      others: [{ subscriptionId: 's2', count: '6', unit: 'month' }] }));
    expect(body.purchases[0]).toMatchObject({
      kind: 'bundle', price: { amount: '88.99', currency: 'EUR' }, allocatedAmount: '20',
      grants: [
        { subscriptionId: null, duration: { count: 3, unit: 'month' }, source: 'included', startsOn: null },
        { subscriptionId: 's2', duration: { count: 6, unit: 'month' }, source: 'included', startsOn: null },
      ],
    });
  });
});

describe('steps and paths', () => {
  it('gives each offer its steps', () => {
    expect(stepsFor('recurring')).toEqual(['type', 'common', 'offer', 'summary']);
    expect(stepsFor('bundle')).toEqual(['type', 'common', 'offer', 'then', 'summary']);
    expect(thenMode({ offer: 'trial', trialVariant: 'launch' })).toBe('required');
    expect(thenMode({ offer: 'gift', trialVariant: 'free' })).toBe('optional');
    expect(thenMode({ offer: 'recurring', trialVariant: 'free' })).toBe('none');
  });

  // Review Focus 4: a refusal comes back to the step and the row that own it.
  it('brings each server path back to its step and field', () => {
    expect(stepOfPath('recurring', 'free', 'phases[0].price.amount')).toBe('offer');
    expect(stepOfPath('prepaid', 'free', 'phases[0].price.amount')).toBe('then');
    expect(stepOfPath('trial', 'launch', 'phases[0].maxCycles')).toBe('offer');
    expect(stepOfPath('trial', 'launch', 'phases[1].price.amount')).toBe('then');
    expect(stepOfPath('bundle', 'free', 'purchases[0].grants[2].subscriptionId')).toBe('offer');
    expect(stepOfPath('bundle', 'free', 'owners')).toBe('common');
    expect(fieldOf('purchases[0].grants[2].subscriptionId', { offer: 'bundle', trialVariant: 'free' })).toBe('others.1.subscriptionId');
    expect(fieldOf('purchases[0].grants[1].duration.count', { offer: 'prepaid', trialVariant: 'free' })).toBe('bonusCount');
    expect(fieldOf('phases[0].price.amount', { offer: 'trial', trialVariant: 'launch' })).toBe('launchAmount');
    expect(fieldOf('phases[1].startsOn', { offer: 'trial', trialVariant: 'launch' })).toBe('nextStartsOn');
    expect(fieldOf('phases[0].price.amount', { offer: 'gift', trialVariant: 'free' })).toBe('amount');
    expect(fieldOf('purchases[0].allocatedAmount', { offer: 'bundle', trialVariant: 'free' })).toBe('allocated');
  });
});
