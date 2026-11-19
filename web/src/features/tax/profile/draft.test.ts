import { describe, expect, it } from 'vitest';
import { ADULT, estimate } from '../taxTestData';
import { countChanges, draftErrors, initialDraft, profileBody, suggestedDeficit } from './draft';

describe('profile draft', () => {
  it('sends the saved profile back unchanged', () => {
    expect(profileBody(initialDraft(estimate()))).toEqual({
      statuses: { [ADULT]: 'non_executive' }, dependents: null, childrenUnder6: 2, rentalRegime: null, department: null, pinel: [],
      contributionRates: {}, actualExpenses: {}, perUnusedCeilings: {}, perCeilings: {}, perCeilingsExpiring: {}, perPooling: false,
      exemptIncomes: {}, rentalDeficitCarried: '0.00', childcareOutsideHome: '6000.00', homeEmployment: '0.00', donations: '0.00',
      donationsAid: '0.00', donationsAidLate: '0.00', rentalEnergyWorks: '0.00', capitalScale: false, deductibleCsg: '0.00',
      alimonyPaid: '0.00', alimonyAdultChildren: '0.00', alimonyAdultChildrenCount: 0, capitalLossesCarried: '0.00',
      alternateCustody: 0, cohabiting: false, disabledAdults: [], disabledDependents: 0, alternateUnder6: 0,
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

  it('suggests last year’s estimated deficit when no notice prints one, the notice’s first', () => {
    const loaded = { ...estimate(), rentalProperties: [{ id: 'p', name: 'Studio', acquiredIn: 2020 }], rentalDeficitEstimated: '2000.00' };

    expect(suggestedDeficit(loaded)).toBe('2000.00');
    expect(initialDraft(loaded).amounts.rentalDeficitCarried).toBe('2000,00');
    expect(suggestedDeficit({ ...loaded, carriedFrom: { year: 2025, perCeilings: {}, perCeilingsExpiring: {}, perUnusedCeilings: {}, rentalDeficitCarried: '1500.00' } })).toBe('1500.00');
    expect(suggestedDeficit({ ...loaded, profile: { ...loaded.profile, rentalDeficitCarried: '800.00' } })).toBeUndefined();
  });

  it('names each field whose text the API would refuse', () => {
    const initial = initialDraft(estimate());

    expect(draftErrors(initial)).toEqual({});
    expect(draftErrors({ ...initial, rates: { [ADULT]: '2x' } })).toEqual({ [`rates.${ADULT}`]: 'rate' });
    expect(draftErrors({ ...initial, under6: '' })).toEqual({ under6: 'count' });
    expect(draftErrors({ ...initial, amounts: { ...initial.amounts, donations: '1,23456' } })).toEqual({ 'amounts.donations': 'amount' });
  });

  it('sends the alimony paid with the number of adult children it goes to', () => {
    const initial = initialDraft(estimate());
    const draft = { ...initial, alimonyChildren: '2', amounts: { ...initial.amounts, alimonyAdultChildren: '9000', alimonyPaid: '1200,50' } };

    expect(profileBody(draft)).toMatchObject({ alimonyPaid: '1200.50', alimonyAdultChildren: '9000', alimonyAdultChildrenCount: 2 });
    expect(countChanges(draft, initial)).toBe(3);
    expect(draftErrors({ ...initial, alimonyChildren: '' })).toEqual({ alimonyChildren: 'count' });
  });

  it('sends alternate custody, cohabiting and the disabled adults, each a change', () => {
    const initial = initialDraft(estimate());
    const draft = { ...initial, alternate: '1', cohabiting: true, disabled: [ADULT] };

    expect(profileBody(draft)).toMatchObject({ alternateCustody: 1, cohabiting: true, disabledAdults: [ADULT] });
    expect(countChanges(draft, initial)).toBe(3);
    expect(draftErrors({ ...initial, alternate: 'x' })).toEqual({ alternate: 'count' });
  });

  it('sends the disabled children as a count, a change of its own', () => {
    const initial = initialDraft(estimate());
    const draft = { ...initial, disabledChildren: '1' };

    expect(profileBody(draft)).toMatchObject({ disabledDependents: 1 });
    expect(countChanges(draft, initial)).toBe(1);
    expect(draftErrors({ ...initial, disabledChildren: '-1' })).toEqual({ disabledChildren: 'count' });
  });

  it('sends the children under 6 in alternate custody as a count, a change of its own', () => {
    const initial = initialDraft(estimate());
    const draft = { ...initial, alternateUnder6: '1' };

    expect(profileBody(draft)).toMatchObject({ alternateUnder6: 1 });
    expect(countChanges(draft, initial)).toBe(1);
    expect(draftErrors({ ...initial, alternateUnder6: 'x' })).toEqual({ alternateUnder6: 'count' });
  });

  it('checks the department as the server does and sends it trimmed', () => {
    const initial = initialDraft(estimate());

    for (const code of ['01', '2a', '2B', '75', '974']) expect(draftErrors({ ...initial, department: code })).toEqual({});
    for (const code of ['20', '96', '977', '1']) expect(draftErrors({ ...initial, department: code })).toEqual({ department: 'department' });
    expect(profileBody({ ...initial, department: ' 75 ' })).toMatchObject({ department: '75' });
    expect(countChanges({ ...initial, department: '75' }, initial)).toBe(1);
  });
});
