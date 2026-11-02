import type { TFunction } from 'i18next';
import { expect, it } from 'vitest';
import { assessmentDefaults, assessmentSchema } from './assessmentSchema';

const valid = (values: Partial<ReturnType<typeof assessmentDefaults>>) =>
  assessmentSchema(((key: string) => key) as unknown as TFunction, 2).safeParse({ ...assessmentDefaults('person', 2024), ...values }).success;

it('Only_the_net_rental_income_takes_a_minus_sign', () => {
  expect(valid({ rentalIncomeNet: '-1037.00' })).toBe(true);
  expect(valid({ rentalIncomeNet: '-' })).toBe(false);
  expect(valid({ netTax: '-1037.00' })).toBe(false);
});
