import { screen, within } from '@testing-library/react';
import { expect } from 'vitest';

/** A page without its `PageHeader` yet (loading, refused): the top bar's trail leads to the list, the page has no inline « Retour … ». */
export function expectTrailBack(label: string, href: string) {
  const trail = screen.getByRole('navigation', { name: 'Fil d’Ariane' });
  expect(within(trail).getByRole('link', { name: label })).toHaveAttribute('href', href);
  expect(within(screen.getByRole('main')).queryByRole('link', { name: /^Retour/ })).not.toBeInTheDocument();
}
