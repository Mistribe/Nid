import '@testing-library/jest-dom/vitest';
import '@/app/i18n';
import { cleanup, configure } from '@testing-library/react';
import { afterAll, afterEach, beforeAll, vi } from 'vitest';
import { server } from './server';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false, media: query, onchange: null,
    addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {},
    dispatchEvent: () => false,
  }),
});

// jsdom has no layout, so it has nothing to scroll; the router calls this on every navigation.
window.scrollTo = () => {};
Element.prototype.scrollIntoView = () => {};

// Radix (Select, Popover, …) calls these pointer-capture methods, which jsdom lacks.
Element.prototype.hasPointerCapture = () => false;
Element.prototype.releasePointerCapture = () => {};
Element.prototype.setPointerCapture = () => {};

// jsdom has no modal dialog: open and close are enough for the tests; focus trapping and Escape are the browser's.
HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) { this.open = true; };
HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
  if (!this.open) return;
  this.open = false;
  this.dispatchEvent(new Event('close'));
};

// jsdom has no layout, so Recharts' ResponsiveContainer would measure 0x0 and draw nothing: report a fixed size
// (800×256, for every observer, whatever the element).
globalThis.ResizeObserver = class {
  constructor(private readonly callback: ResizeObserverCallback) {}
  observe(target: Element) {
    const contentRect = { width: 800, height: 256, top: 0, left: 0, right: 800, bottom: 256, x: 0, y: 0, toJSON: () => ({}) };
    this.callback([{ target, contentRect } as ResizeObserverEntry], this);
  }
  unobserve() {}
  disconnect() {}
};

// findBy* waits 1 s by default: a lazy route, cmdk or the confirmation dialog imported cold in a worker took over 5 s under load.
configure({ asyncUtilTimeout: 10_000 });

// Any request no test expects is a failure, not a silent network call.
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));

afterEach(() => {
  cleanup();
  localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
  document.documentElement.className = '';
  // Files share their worker's window (isolate: false): a Radix modal's scroll lock and focus guards, or Recharts'
  // measuring span, would otherwise reach the next file's body.
  document.body.innerHTML = '';
  for (const name of document.body.getAttributeNames()) document.body.removeAttribute(name);
  server.resetHandlers();
});

afterAll(() => {
  server.close();
  // The next file runs in the same worker (isolate: false): undo what this one stubbed, spied on or froze.
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});
