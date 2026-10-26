import { describe, expect, it } from 'vitest';
import { localDateOf, parseNotificationLink } from './link';

describe('notification links', () => {
  it('splits the path from its search', () => {
    expect(parseNotificationLink('/properties/abc?tab=rental')).toEqual({ to: '/properties/abc', search: { tab: 'rental' } });
    expect(parseNotificationLink('/loans/abc')).toEqual({ to: '/loans/abc', search: {} });
  });

  it('reads the arrival on the local calendar', () => {
    const at = new Date(2026, 9, 21, 0, 30);
    expect(localDateOf(at.toISOString())).toBe('2026-10-21');
  });
});
