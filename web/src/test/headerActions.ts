import { screen } from '@testing-library/react';
import { expect } from 'vitest';
import type { renderApp } from './renderApp';

type User = Awaited<ReturnType<typeof renderApp>>['user'];

/** Opens the page's « ⋯ » menu the way a keyboard user does: focus its trigger, press Enter. */
export async function openMore(user: User) {
  screen.getByRole('button', { name: 'Autres actions' }).focus();
  await user.keyboard('{Enter}');
}

/**
 * Spec §4.1: a page header has at most one primary and one secondary action; everything else sits in a `⋯` menu.
 * Counts the visible buttons and links of the header's actions (not the breadcrumb, nor the figures under the title), leaving
 * out an open menu and an icon-only menu trigger.
 */
export function expectHeaderActionRule(container: HTMLElement) {
  expect(container.querySelector('header[data-page-header]'), 'a page header').not.toBeNull();
  const actions = [...container.querySelectorAll<HTMLElement>('header[data-page-header] [data-page-actions] :is(button, a[href])')].filter((el) =>
    !el.closest('[role="menu"], [hidden], [aria-hidden="true"]')
    && !(el.getAttribute('aria-haspopup') === 'menu' && !el.textContent?.trim()));
  const names = actions.map((el) => el.textContent?.trim() || el.getAttribute('aria-label'));
  expect(actions.length, `header actions: ${names.join(', ')}`).toBeLessThanOrEqual(2);
  // Button's default variant is the only one with the `bg-primary` class (`hover:bg-primary/90` is another token).
  expect(actions.filter((el) => el.classList.contains('bg-primary')).length, 'primary actions').toBeLessThanOrEqual(1);
}
