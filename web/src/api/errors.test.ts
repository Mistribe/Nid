import { describe, expect, it } from 'vitest';
import { pathOf, present, samePath, toProblem } from './errors';

describe('present', () => {
  it.each([
    [401, 'unauthenticated'], [403, 'forbidden'], [404, 'not_found'], [409, 'conflict'],
    [422, 'validation'], [400, 'bad_request'], [500, 'unexpected'], [0, 'unexpected'],
  ] as const)('maps %i to %s', (status, kind) => expect(present({ status }).kind).toBe(kind));

  it('never says why something is not found: absent and invisible look the same', () =>
    expect(present({ status: 404, detail: 'subscription 0190… not visible' }).detail).toBeUndefined());

  it('keeps the server sentence (RFC 9457 detail) for a refused value', () =>
    expect(present({ status: 422, code: 'core.preference_invalid', detail: 'A mode is system, light or dark.' }).detail)
      .toBe('A mode is system, light or dark.'));

  it('says a demo refusal in its own words, without the server sentence', () =>
    expect(present({ status: 403, code: 'demo.unavailable', detail: 'The demo does not…' })).toEqual({ kind: 'demo', requestId: undefined }));

  it('carries the request id for support', () =>
    expect(present({ status: 500, traceId: '00-abc-01' }).requestId).toBe('00-abc-01'));
});

describe('toProblem', () => {
  it('keeps a Problem Details body', () => expect(toProblem({ status: 409, code: 'x' })).toEqual({ status: 409, code: 'x' }));
  it('turns anything else (network failure) into status 0', () => expect(toProblem(new TypeError('Failed to fetch')).status).toBe(0));

  it('reads the RFC 9457 errors as fields with paths, codes and sentences', () =>
    expect(toProblem({
      status: 422, code: 'request.invalid',
      errors: [
        { detail: 'A number is required.', pointer: '#/payload/movements/3/amount', code: 'request.invalid' },
        { detail: 'The period is closed.' },
      ],
    }).errors).toEqual([
      { path: 'payload.movements[3].amount', code: 'request.invalid', message: 'A number is required.' },
      { path: '', code: 'request.invalid', message: 'The period is closed.' },
    ]));

  it('reads a body already read the same way', () => {
    const read = toProblem({ status: 422, errors: [{ detail: 'Required.', pointer: '#/name', code: 'x' }] });
    expect(toProblem(read)).toEqual(read);
  });
});

describe('pathOf', () => {
  it.each([
    ['#/amount', 'amount'],
    ['#/payload/movements/3/amount', 'payload.movements[3].amount'],
    ['#/weekTypes/0/days/2', 'weekTypes[0].days[2]'],
    ['#/a~1b/c~0d', 'a/b.c~d'],
    ['#/first%20name', 'first name'],
    [null, ''],
    ['#', ''],
  ])('reads %s as %s', (pointer, path) => expect(pathOf(pointer)).toBe(path));
});

describe('samePath', () => {
  it('reads a numeric key and an index alike, since a pointer cannot tell them apart', () => {
    expect(samePath('inflationByYear[2026]', 'inflationByYear.2026')).toBe(true);
    expect(samePath('classRatesByYear.equity[2027]', 'classRatesByYear.equity.2027')).toBe(true);
    expect(samePath('inflationByYear[2026]', 'inflationByYear.2027')).toBe(false);
  });
});
