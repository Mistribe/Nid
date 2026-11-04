import { expect, it } from 'vitest';
import { NAV } from './nav';

it('every entry names its tile, and no two modules share a slot', () => {
  for (const item of NAV) expect(item.tile, item.to).toBeDefined();
  const slots = NAV.map((i) => i.tile).filter((t) => typeof t === 'number');
  expect(new Set(slots).size).toBe(slots.length);
  const of = (to: string) => NAV.find((i) => i.to === to)?.tile;
  expect([of('/investments'), of('/loans'), of('/properties'), of('/goals'), of('/childcare'), of('/income'), of('/scenarios'), of('/subscriptions'), of('/tax'), of('/dashboard')])
    .toEqual([1, 2, 3, 4, 5, 6, 7, 8, 'neutral', 'primary']);
});
