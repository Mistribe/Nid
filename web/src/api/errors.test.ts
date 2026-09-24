import { describe, expect, it } from 'vitest';
import { present, toProblem } from './errors';

describe('present', () => {
  it.each([
    [401, 'unauthenticated'], [403, 'forbidden'], [404, 'not_found'], [409, 'conflict'],
    [422, 'validation'], [400, 'bad_request'], [500, 'unexpected'], [0, 'unexpected'],
  ] as const)('maps %i to %s', (status, kind) => expect(present({ status }).kind).toBe(kind));

  it('never says why something is not found: absent and invisible look the same', () =>
    expect(present({ status: 404, title: 'subscription 0190… not visible' }).detail).toBeUndefined());

  it('keeps the server message for a refused value', () =>
    expect(present({ status: 422, code: 'core.preference_invalid', title: 'A mode is system, light or dark.' }).detail)
      .toBe('A mode is system, light or dark.'));

  it('carries the request id for support', () =>
    expect(present({ status: 500, traceId: '00-abc-01' }).requestId).toBe('00-abc-01'));
});

describe('toProblem', () => {
  it('keeps a Problem Details body', () => expect(toProblem({ status: 409, code: 'x' })).toEqual({ status: 409, code: 'x' }));
  it('turns anything else (network failure) into status 0', () => expect(toProblem(new TypeError('Failed to fetch')).status).toBe(0));
});
