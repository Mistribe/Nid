import { expect } from 'vitest';

/** A filter's visible label comes before its control, both inside the same `FilterField`. */
export function expectLabelBefore(label: HTMLElement, control: HTMLElement) {
  const field = label.closest('[data-filter-field]');
  expect(field).not.toBeNull();
  expect(field).toContainElement(control);
  expect(label.compareDocumentPosition(control) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
}
