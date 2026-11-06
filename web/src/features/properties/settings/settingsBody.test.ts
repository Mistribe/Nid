import { describe, expect, it } from 'vitest';
import { propertyDetail } from '../testData';
import { settingsValuesOf, toEditBody, toWireBody } from './settingsBody';

const values = settingsValuesOf(propertyDetail());

describe('toEditBody', () => {
  it('is null when nothing changed, the server’s amounts and a trimmed name included', () => {
    expect(values).toMatchObject({ purchasePrice: '250000,00', surfaceM2: '85,50', staleAfterDays: '', note: '' });
    expect(toEditBody(propertyDetail(), { ...values, name: ' Maison de Nantes ' })).toBeNull();
  });

  it('sends only what changed: amounts with a dot, the threshold as a number', () => {
    expect(toEditBody(propertyDetail(), {
      ...values, type: 'rental', purchasePrice: '255000', notaryFees: '19500,5', valuationCadence: 'manual', staleAfterDays: '730', note: 'toiture',
    })).toEqual({ type: 'rental', purchasePrice: '255000', notaryFees: '19500.5', valuationCadence: 'manual', staleAfterDays: 730, note: 'toiture' });
  });

  it('says an emptied optional field with its clear flag', () => {
    const before = propertyDetail({ staleAfterDays: 730, note: 'x' });
    expect(toEditBody(before, { ...settingsValuesOf(before), surfaceM2: '', locationLabel: '  ', staleAfterDays: '', note: '' }))
      .toEqual({ clearSurfaceM2: true, clearLocationLabel: true, clearStaleAfterDays: true, clearNote: true });
  });

  it('sends a rental start typed, and its clearing once emptied', () => {
    expect(values.rentalStartsOn).toBe('');
    expect(toEditBody(propertyDetail(), { ...values, rentalStartsOn: '2021-01-01' })).toEqual({ rentalStartsOn: '2021-01-01' });
    const before = propertyDetail({ rentalStartsOn: '2021-01-01' });
    expect(toEditBody(before, settingsValuesOf(before))).toBeNull();
    expect(toEditBody(before, { ...settingsValuesOf(before), rentalStartsOn: '' })).toEqual({ clearRentalStartsOn: true });
  });
});

describe('the energy class', () => {
  it('is sent as a letter once chosen and cleared once emptied', () => {
    expect(values.energyClass).toBe('');
    expect(toEditBody(propertyDetail(), { ...values, energyClass: 'F' })).toEqual({ energyClass: 'F' });
    const before = propertyDetail({ energyClass: 'D' });
    expect(toEditBody(before, settingsValuesOf(before))).toBeNull();
    expect(toEditBody(before, { ...settingsValuesOf(before), energyClass: '' })).toEqual({ clearEnergyClass: true });
  });
});

describe('the postal address', () => {
  it('is sent trimmed once typed and cleared once emptied', () => {
    expect(values.postalAddress).toBe('');
    expect(toEditBody(propertyDetail(), { ...values, postalAddress: ' 12 rue A\n75011 Paris ' })).toEqual({ postalAddress: '12 rue A\n75011 Paris' });
    const before = propertyDetail({ postalAddress: '12 rue A' });
    expect(toEditBody(before, settingsValuesOf(before))).toBeNull();
    expect(toEditBody(before, { ...settingsValuesOf(before), postalAddress: '' })).toEqual({ clearPostalAddress: true });
  });
});

describe('toWireBody', () => {
  it('states the version and every clear flag, which the wire type requires', () => {
    expect(toWireBody({ name: 'Maison' }, 7)).toEqual({
      name: 'Maison', version: 7, clearSurfaceM2: false, clearLocationLabel: false, clearStaleAfterDays: false,
      clearRentalStartsOn: false, clearNote: false, clearEnergyClass: false, clearPostalAddress: false,
    });
  });
});
