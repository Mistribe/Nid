import { describe, expect, it } from 'vitest';
import { detail, eur } from '../testData';
import { toEditBody, toWireBody } from './settingsBody';

const values = (over = {}) => ({ displayName: 'PEA', kind: 'pea', institution: 'Banque', accountHint: '4521', valuationCadence: 'monthly', staleAfterDays: '', ucShareFloorPct: '', note: '', openedOn: '', premiumsBefore2017: '', regulated: 'none', ...over });

describe('toEditBody', () => {
  it('sends nothing when nothing changed', () => expect(toEditBody(detail(), values())).toBeNull());
  it('sends only what changed', () => expect(toEditBody(detail(), values({ displayName: 'PEA Bourse', valuationCadence: 'quarterly' })))
    .toEqual({ displayName: 'PEA Bourse', valuationCadence: 'quarterly' }));
  it('ignores surrounding spaces', () => expect(toEditBody(detail(), values({ displayName: ' PEA ', institution: 'Banque ' }))).toBeNull());
  it('clears an emptied optional field', () => expect(toEditBody(detail(), values({ institution: '', accountHint: '' })))
    .toEqual({ clearInstitution: true, clearAccountHint: true }));
  it('sends a stale threshold as a number and clears it when emptied', () => {
    expect(toEditBody(detail(), values({ staleAfterDays: '30' }))).toEqual({ staleAfterDays: 30 });
    expect(toEditBody(detail({ staleAfterDays: 30 }), values({ staleAfterDays: '' }))).toEqual({ clearStaleAfterDays: true });
    expect(toEditBody(detail({ staleAfterDays: 30 }), values({ staleAfterDays: '30' }))).toBeNull();
    expect(toEditBody(detail(), values({ ucShareFloorPct: '30,5' }))).toEqual({ ucShareFloorPct: 30.5 });
    expect(toEditBody(detail({ ucShareFloorPct: 30 }), values({ ucShareFloorPct: '' }))).toEqual({ clearUcShareFloorPct: true });
    expect(toEditBody(detail({ ucShareFloorPct: 30 }), values({ ucShareFloorPct: '30' }))).toBeNull();
  });
  it('sets a note, and clears it once emptied', () => {
    expect(toEditBody(detail(), values({ note: 'Plafond atteint' }))).toEqual({ note: 'Plafond atteint' });
    expect(toEditBody(detail({ note: 'x' }), values({ note: '' }))).toEqual({ clearNote: true });
  });
});

describe('premiums before 2017', () => {
  const life = (over = {}) => detail({ kind: 'life_insurance', ...over });
  const lifeValues = (over = {}) => values({ kind: 'life_insurance', ...over });
  it('sends the typed amount as a decimal string', () => {
    expect(toEditBody(life(), lifeValues({ premiumsBefore2017: '12 000,50' }))).toEqual({ premiumsBefore2017: '12000.50' });
  });
  it('clears it once emptied, and sends nothing when unchanged', () => {
    expect(toEditBody(life({ premiumsBefore2017: eur('12000.5000') }), lifeValues())).toEqual({ clearPremiumsBefore2017: true });
    expect(toEditBody(life({ premiumsBefore2017: eur('12000.5000') }), lifeValues({ premiumsBefore2017: '12000.5000' }))).toBeNull();
  });
  it('never sends it with a kind change', () => {
    expect(toEditBody(detail(), values({ kind: 'life_insurance', premiumsBefore2017: '100' }))).toEqual({ kind: 'life_insurance' });
    expect(toEditBody(life({ premiumsBefore2017: eur('100') }), lifeValues({ kind: 'pea', premiumsBefore2017: '' }))).toEqual({ kind: 'pea' });
    expect(toEditBody(life(), lifeValues({ kind: 'tontine', premiumsBefore2017: '100' }))).toEqual({ kind: 'tontine' });
  });
  it('applies to a tontine too', () => {
    expect(toEditBody(detail({ kind: 'tontine' }), values({ kind: 'tontine', premiumsBefore2017: '5000' }))).toEqual({ premiumsBefore2017: '5000' });
  });
  it('sets and clears the opening date', () => {
    expect(toEditBody(detail(), values({ openedOn: '2015-03-02' }))).toEqual({ openedOn: '2015-03-02' });
    expect(toEditBody(detail({ openedOn: '2015-03-02' }), values())).toEqual({ clearOpenedOn: true });
  });
});

describe('a regulated livret', () => {
  const livret = (over = {}) => detail({ kind: 'savings', ...over });
  it('is set, changed and cleared on a savings envelope', () => {
    expect(toEditBody(livret(), values({ kind: 'savings', regulated: 'livret_a' }))).toEqual({ regulated: 'livret_a' });
    expect(toEditBody(livret({ regulated: 'livret_a' }), values({ kind: 'savings', regulated: 'livret_a' }))).toBeNull();
    expect(toEditBody(livret({ regulated: 'livret_a' }), values({ kind: 'savings', regulated: 'none' }))).toEqual({ clearRegulated: true });
  });
  it('is never sent off savings: the server drops it', () =>
    expect(toEditBody(livret({ regulated: 'ldds' }), values({ kind: 'checking', regulated: 'ldds' }))).toEqual({ kind: 'checking' }));
});

describe('toWireBody', () => {
  it('states every clear flag the body does not set', () => expect(toWireBody({ displayName: 'PEA Bourse', clearNote: true })).toEqual({
    displayName: 'PEA Bourse', clearInstitution: false, clearAccountHint: false, clearOpenedOn: false, clearStaleAfterDays: false, clearNote: true, clearUcShareFloorPct: false,
  }));
});
