import { describe, expect, it } from 'vitest';
import { labelColour, labelSlot } from './labelColour';

describe('labelSlot', () => {
  it.each([['#3679cd', 1], ['#e46e42', 2], ['#37ad7d', 3], ['#e8a332', 4], ['#e280a4', 5], ['#1f811c', 6], ['#4a3e9f', 7], ['#db534f', 8]])(
    'a palette colour %s maps to its own slot %s',
    (hex, slot) => expect(labelSlot(hex)).toBe(slot),
  );

  it.each([['#1e88e5', 1], ['#ff9800', 4], ['#9c27b0', 7], ['#e91e63', 8], ['#4caf50', 3]])(
    'a catalog colour %s maps to the nearest slot %s',
    (hex, slot) => expect(labelSlot(hex)).toBe(slot),
  );

  it.each(['#9e9e9e', '#607D8B', '#000000', '#ffffff', '#f5f5f5'])('a grey %s is neutral', (hex) => expect(labelSlot(hex)).toBe('neutral'));

  it.each([null, undefined, 'red', '#abc', '#12345g'])('a malformed colour %s is neutral', (hex) => expect(labelSlot(hex)).toBe('neutral'));

  it('labelColour gives the CSS variable', () => {
    expect(labelColour('#3679cd')).toBe('var(--chart-1)');
    expect(labelColour('#9e9e9e')).toBe('var(--muted-foreground)');
  });
});
