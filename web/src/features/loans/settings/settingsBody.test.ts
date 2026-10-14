import { describe, expect, it } from 'vitest';
import { loanDetail } from '../testData';
import { toEditBody, toWireBody } from './settingsBody';

const values = { name: 'Prêt maison', lender: 'Banque', note: '', propertyId: '' };

describe('toEditBody', () => {
  it('links, relinks and unlinks the property financed, and leaves an unchanged link alone (3D1 design §7)', () => {
    expect(toEditBody(loanDetail(), { ...values, propertyId: 'p1' })).toEqual({ propertyId: 'p1' });
    const linked = loanDetail({ propertyId: 'p1', propertyName: null });
    expect(toEditBody(linked, { ...values, propertyId: 'p2' })).toEqual({ propertyId: 'p2' });
    expect(toEditBody(linked, values)).toEqual({ clearProperty: true });
    expect(toEditBody(linked, { ...values, propertyId: 'p1' })).toBeNull();
  });

  it('is null when nothing changed, a trimmed name included', () => {
    expect(toEditBody(loanDetail(), { ...values, name: ' Prêt maison ' })).toBeNull();
  });

  it('sends only what changed, trimmed', () => {
    expect(toEditBody(loanDetail(), { ...values, name: 'Résidence ', note: 'taux renégocié' })).toEqual({ name: 'Résidence', note: 'taux renégocié' });
  });

  it('says an emptied optional field with its clear flag', () => {
    expect(toEditBody(loanDetail(), { ...values, lender: '  ' })).toEqual({ clearLender: true });
    expect(toEditBody(loanDetail({ note: 'x' }), values)).toEqual({ clearNote: true });
  });
});

describe('toWireBody', () => {
  it('states both clear flags, which the wire type requires', () => {
    expect(toWireBody({ name: 'Résidence' })).toEqual({ name: 'Résidence', clearLender: false, clearNote: false, clearProperty: false });
  });
});
