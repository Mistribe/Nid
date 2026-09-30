import '@testing-library/jest-dom/vitest';
import '@/app/i18n';
import { cleanup } from '@testing-library/react';
import { afterAll, afterEach, beforeAll } from 'vitest';
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

// jsdom has no modal dialog: open and close are enough for the tests; focus trapping and Escape are the browser's.
HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) { this.open = true; };
HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
  if (!this.open) return;
  this.open = false;
  this.dispatchEvent(new Event('close'));
};

// jsdom has no layout, so Recharts' ResponsiveContainer would measure 0x0 and draw nothing: report a fixed size.
globalThis.ResizeObserver = class {
  constructor(private readonly callback: ResizeObserverCallback) {}
  observe(target: Element) {
    const contentRect = { width: 800, height: 256, top: 0, left: 0, right: 800, bottom: 256, x: 0, y: 0, toJSON: () => ({}) };
    this.callback([{ target, contentRect } as ResizeObserverEntry], this);
  }
  unobserve() {}
  disconnect() {}
};

// Any request no test expects is a failure, not a silent network call.
beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));

afterEach(() => {
  cleanup();
  localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
  document.documentElement.className = '';
  server.resetHandlers();
});

afterAll(() => server.close());
