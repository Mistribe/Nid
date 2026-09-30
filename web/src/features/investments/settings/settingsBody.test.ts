import { describe, expect, it } from 'vitest';
import { detail } from '../testData';
import { toEditBody, toWireBody } from './settingsBody';

const values = (over = {}) => ({ displayName: 'PEA', kind: 'pea', institution: 'Banque', accountHint: '4521', valuationCadence: 'monthly', staleAfterDays: '', note: '', ...over });

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
  });
  it('sets a note, and clears it once emptied', () => {
    expect(toEditBody(detail(), values({ note: 'Plafond atteint' }))).toEqual({ note: 'Plafond atteint' });
    expect(toEditBody(detail({ note: 'x' }), values({ note: '' }))).toEqual({ clearNote: true });
  });
});

describe('toWireBody', () => {
  it('states every clear flag the body does not set', () => expect(toWireBody({ displayName: 'PEA Bourse', clearNote: true })).toEqual({
    displayName: 'PEA Bourse', clearInstitution: false, clearAccountHint: false, clearOpenedOn: false, clearStaleAfterDays: false, clearNote: true,
  }));
});
