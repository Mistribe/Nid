import { render, screen } from '@testing-library/react';
import { expect, it } from 'vitest';
import { EmptyState } from './EmptyState';

it('shows the module tile, large, then the title as an h2 and the sentence', () => {
  const { container } = render(<EmptyState module="/loans" title="No loan" body="Add your first loan." />);
  expect(container.querySelector('[data-tile="2"]')).toHaveClass('size-12');
  expect(screen.getByRole('heading', { level: 2, name: 'No loan' })).toHaveClass('text-title');
  expect(screen.getByText('Add your first loan.')).toBeInTheDocument();
});

it('hides the tile and the arcs from assistive technology', () => {
  const { container } = render(<EmptyState module="/loans" title="No loan" body="Add your first loan." action={<a href="/x">Add</a>} />);
  const nodes = container.querySelectorAll('svg, [data-tile]');
  expect(nodes).toHaveLength(3);
  for (const n of nodes) expect(n.closest('[aria-hidden="true"]')).not.toBeNull();
  expect(container.firstElementChild?.textContent).toBe('No loanAdd your first loan.Add');
});

it('lays out several actions side by side', () => {
  render(<EmptyState module="/loans" title="t" body="b" action={<><a href="/a">A</a><a href="/b">B</a></>} />);
  expect(screen.getAllByRole('link')).toHaveLength(2);
});

it('takes a heading level for a screen where the title sits under an h2', () => {
  render(<EmptyState module="/loans" title="No loan" body="b" level={3} />);
  expect(screen.getByRole('heading', { level: 3, name: 'No loan' })).toBeInTheDocument();
});
