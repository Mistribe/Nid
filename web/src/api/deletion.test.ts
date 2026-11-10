import { describe, expect, it } from 'vitest';
import strings from '@/locales/fr.deletion.json';
import { DELETABLE, isClosable } from './deletion';

describe('DELETABLE', () => {
  it.each(Object.entries(DELETABLE))('%s has a preview route and a French close hint', (kind, entry) => {
    expect(entry.preview).toBe(`${entry.remove}/deletion`);
    const hint = (strings.deletion.close_hint as Record<string, string>)[kind];
    if (!isClosable(kind as keyof typeof DELETABLE)) expect(hint).toBeUndefined(); else expect(hint).toBeTruthy();
  });
});
