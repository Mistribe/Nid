import axe from 'axe-core';
import { expect } from 'vitest';

/**
 * jsdom computes no colours: contrast is proven by the theme test instead (Task 6). It loads no frame either: axe checks a
 * frame's title, not its content (a document's preview, issue #413).
 */
export async function expectNoAxeViolations(container: Element): Promise<void> {
  const results = await axe.run(container, { iframes: false, rules: { 'color-contrast': { enabled: false }, region: { enabled: false } } });
  expect(results.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`)).toEqual([]);
}
