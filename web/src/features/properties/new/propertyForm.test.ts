import type { TFunction } from 'i18next';
import { describe, expect, it } from 'vitest';
import { newPropertyPath, newPropertySchema, toCreateBody, type NewPropertyValues } from './propertyForm';

const t = ((key: string) => key) as unknown as TFunction;

const valid = (over: Partial<NewPropertyValues> = {}): NewPropertyValues => ({
  name: 'Maison de Nantes', currency: 'EUR', note: '', visibility: 'shared', owners: [{ personId: 'alice', share: '100' }],
  type: 'primary_residence', purchaseDate: '2020-06-15', purchasePrice: '250000', notaryFees: '19000', agencyFees: '0',
  otherAcquisitionCosts: '0', surfaceM2: '', locationLabel: '', valuationCadence: 'yearly', staleAfterDays: '', ...over,
});

const issues = (v: NewPropertyValues) => {
  const r = newPropertySchema(t).safeParse(v);
  return r.success ? [] : r.error.issues.map((i) => `${i.path.join('.')}: ${i.message}`);
};

describe('the schema', () => {
  it('accepts fees of zero and no optional field, amounts with a comma or a dot', () => {
    expect(issues(valid())).toEqual([]);
    expect(issues(valid({ purchasePrice: '250000,50', notaryFees: '19000.5', surfaceM2: '85,5', staleAfterDays: '3650' }))).toEqual([]);
  });

  it.each([
    [{ purchasePrice: '0' }, 'purchasePrice: loan_form.amount_format'],
    [{ purchasePrice: '250 000' }, 'purchasePrice: loan_form.amount_format'],
    [{ notaryFees: '19000,123' }, 'notaryFees: property_form.fee_format'],
    [{ agencyFees: '-1' }, 'agencyFees: property_form.fee_format'],
    [{ surfaceM2: '0' }, 'surfaceM2: property_form.surface_format'],
    [{ staleAfterDays: '0' }, 'staleAfterDays: property_form.stale_range'],
    [{ staleAfterDays: '3651' }, 'staleAfterDays: property_form.stale_range'],
    [{ purchaseDate: '1899-12-31' }, 'purchaseDate: property_form.date_range'],
    [{ purchaseDate: '2999-01-01' }, 'purchaseDate: field_error.core_date_in_future'],
    [{ locationLabel: 'x'.repeat(101) }, 'locationLabel: property_form.location_too_long'],
    [{ currency: 'eu' }, 'currency: field_error.request_currency'],
  ] as [Partial<NewPropertyValues>, string][])('refuses %j as the server would', (over, issue) => {
    expect(issues(valid(over))).toEqual([issue]);
  });
});

describe('the wire', () => {
  it('sends the amounts with a dot, the location trimmed, and leaves out what is empty', () => {
    expect(toCreateBody(valid())).toEqual({
      name: 'Maison de Nantes', currency: 'EUR', visibility: 'shared', owners: [{ personId: 'alice', shareBp: 10000 }],
      type: 'primary_residence', purchaseDate: '2020-06-15', purchasePrice: '250000', notaryFees: '19000', agencyFees: '0',
      otherAcquisitionCosts: '0', valuationCadence: 'yearly',
    });
    expect(toCreateBody(valid({ purchasePrice: '250000,50', surfaceM2: '85,5', locationLabel: ' Nantes ', staleAfterDays: '730', note: 'achat' })))
      .toMatchObject({ purchasePrice: '250000.50', surfaceM2: '85.5', locationLabel: 'Nantes', staleAfterDays: 730, note: 'achat' });
  });
});

describe('server paths', () => {
  it.each([
    ['name', 'name'], ['currency', 'currency'], ['visibility', 'visibility'], ['owners', 'owners'], ['owners[1].shareBp', 'owners'],
    ['purchaseDate', 'purchaseDate'], ['notaryFees', 'notaryFees'], ['surfaceM2', 'surfaceM2'], ['staleAfterDays', 'staleAfterDays'],
  ])('places %s under %s', (path, field) => {
    expect(newPropertyPath(path)).toBe(field);
  });

  it('leaves a path no field owns to the page', () => {
    expect(newPropertyPath('rentalStartsOn')).toBeUndefined();
    expect(newPropertyPath('version')).toBeUndefined();
  });
});
