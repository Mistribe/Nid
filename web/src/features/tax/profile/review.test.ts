import { describe, expect, it } from 'vitest';
import { estimate, result } from '../taxTestData';
import { reviewReasons } from './review';

const per = (source: string) => ({ ceiling: '3980.00', source, expiring: '0.00', contributions: '0.00', deducted: '0.00', excess: '0.00', remaining: '3980.00', minimum: '0.00', minimumEstimated: false });

describe('reviewReasons', () => {
  it('asks nothing of a profile saved for the year', () => {
    expect(reviewReasons(estimate())).toEqual({});
  });

  it('asks to review the expenses of a profile carried over', () => {
    expect(reviewReasons({ ...estimate(), profileFrom: 2025 })).toEqual({ depenses: 'carried_over' });
  });

  it('asks to review the retirement savings while a ceiling is estimated', () => {
    const declarant = result().declarants[0]!;
    const estimated = { ...estimate(), result: result({ declarants: [{ ...declarant, per: per('estimated') }] }) };
    const notice = { ...estimate(), result: result({ declarants: [{ ...declarant, per: per('notice') }] }) };

    expect(reviewReasons(estimated)).toEqual({ retraite: 'ceiling_estimated' });
    expect(reviewReasons(notice)).toEqual({});
  });
});
