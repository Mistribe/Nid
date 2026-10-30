import { describe, expect, it } from 'vitest';
import { ADULT, estimate } from '../taxTestData';
import { countChanges, draftErrors, initialDraft, profileBody } from './draft';

describe('profile draft', () => {
  it('sends the saved profile back unchanged', () => {
    expect(profileBody(initialDraft(estimate()))).toEqual({
      statuses: { [ADULT]: 'non_executive' }, dependents: null, childrenUnder6: 2, rentalRegime: null, pinel: [],
      contributionRates: {}, actualExpenses: {}, perUnusedCeilings: {}, perCeilings: {}, perCeilingsExpiring: {}, perPooling: false,
      exemptIncomes: {}, rentalDeficitCarried: '0.00', childcareOutsideHome: '6000.00', homeEmployment: '0.00', donations: '0.00',
      donationsAid: '0.00', donationsAidLate: '0.00', rentalEnergyWorks: '0.00',
    });
  });

  it('sends typed values as the API reads them and counts each edited field', () => {
    const initial = initialDraft(estimate());
    const draft = { ...initial, rates: { [ADULT]: '23,5' }, amounts: { ...initial.amounts, homeEmployment: '9000' } };

    expect(profileBody(draft)).toMatchObject({ contributionRates: { [ADULT]: '23.5' }, homeEmployment: '9000' });
    expect(countChanges(draft, initial)).toBe(2);
  });

  it('drops a cleared saved value and counts it as one change', () => {
    const loaded = estimate();
    loaded.profile.perCeilings = { [ADULT]: '4637.00' };
    const initial = initialDraft(loaded);
    const draft = { ...initial, ceilings: { [ADULT]: '' } };

    expect(profileBody(draft)).toMatchObject({ perCeilings: {} });
    expect(countChanges(draft, initial)).toBe(1);
  });

  it('names each field whose text the API would refuse', () => {
    const initial = initialDraft(estimate());

    expect(draftErrors(initial)).toEqual({});
    expect(draftErrors({ ...initial, rates: { [ADULT]: '2x' } })).toEqual({ [`rates.${ADULT}`]: 'rate' });
    expect(draftErrors({ ...initial, under6: '' })).toEqual({ under6: 'count' });
    expect(draftErrors({ ...initial, amounts: { ...initial.amounts, donations: '1,23456' } })).toEqual({ 'amounts.donations': 'amount' });
  });
});
