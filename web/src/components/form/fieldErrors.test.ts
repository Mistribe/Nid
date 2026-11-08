import { describe, expect, it } from 'vitest';
import { fieldErrorsOf, messageFor, stepOf } from '@/components/form/fieldErrors';

describe('fieldErrorsOf', () => {
  it('reads the errors of a 422', () =>
    expect(fieldErrorsOf({ status: 422, code: 'request.invalid', errors: [{ path: 'phase.price.amount', code: 'request.amount', message: 'm' }] }))
      .toEqual([{ path: 'phase.price.amount', code: 'request.amount', message: 'm' }]));

  it('has none for anything else', () => {
    expect(fieldErrorsOf({ status: 409 })).toEqual([]);
    expect(fieldErrorsOf(new TypeError('offline'))).toEqual([]);
  });
});

describe('stepOf', () => {
  const steps = { 0: ['displayName', 'visibility', 'owners', 'providerKey', 'users', 'note'], 1: ['phase'] };
  it('finds the step owning a path', () => {
    expect(stepOf('providerKey', steps)).toBe(0);
    expect(stepOf('owners[0].shareBp', steps)).toBe(0);
    expect(stepOf('phase.price.amount', steps)).toBe(1);
  });
  it('is undefined for a path no step owns', () => expect(stepOf('elsewhere', steps)).toBeUndefined());
});

describe('messageFor', () => {
  const t = ((key: string) => `fr:${key}`) as unknown as Parameters<typeof messageFor>[0];
  it('translates a known code, its dots made underscores', () =>
    expect(messageFor(t, { path: 'p', code: 'request.amount', message: 'server' })).toBe('fr:field_error.request_amount'));
  it("keeps the server's sentence for an unknown code", () =>
    expect(messageFor(t, { path: 'p', code: 'core.something_new', message: 'server' })).toBe('server'));
});
