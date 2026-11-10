import { describe, expect, it } from 'vitest';
import strings from '@/locales/fr.deletion.json';
import { DELETABLE } from './deletion';

describe('DELETABLE', () => {
  // A property and a loan have no close action: no hint.
  const NO_CLOSE = ['realestate.delete_property', 'realestate.delete_loan'];
  it.each(Object.entries(DELETABLE))('%s has a preview route and a French close hint', (kind, entry) => {
    expect(entry.preview).toBe(`${entry.remove}/deletion`);
    const hint = (strings.deletion.close_hint as Record<string, string>)[kind];
    if (NO_CLOSE.includes(kind)) expect(hint).toBeUndefined(); else expect(hint).toBeTruthy();
  });
});
