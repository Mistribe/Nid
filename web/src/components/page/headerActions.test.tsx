import { describe, expect, it } from 'vitest';
import { expectHeaderActionRule } from '@/test/headerActions';

const fixture = (buttons: number, primary = 1, figures = '') => {
  const el = document.createElement('div');
  el.innerHTML = `<header data-page-header><nav><a href="/x">Crumb</a></nav><div data-page-actions>${
    Array.from({ length: buttons }, (_, i) => `<button class="${i < primary ? 'bg-primary' : 'border'}">A${i}</button>`).join('')
  }<button aria-haspopup="menu" aria-label="Plus"></button></div>${figures}</header>`;
  return el;
};

describe('expectHeaderActionRule', () => {
  it('accepts two actions, a breadcrumb and a ⋯ trigger', () => {
    expect(() => expectHeaderActionRule(fixture(2))).not.toThrow();
  });
  it('refuses three actions', () => {
    expect(() => expectHeaderActionRule(fixture(3))).toThrow();
  });
  it('refuses two primary actions', () => {
    expect(() => expectHeaderActionRule(fixture(2, 2))).toThrow();
  });
  it('leaves out a button among the figures under the title', () => {
    expect(() => expectHeaderActionRule(fixture(2, 1, '<section><button class="bg-primary">Détail</button></section>'))).not.toThrow();
  });
});
