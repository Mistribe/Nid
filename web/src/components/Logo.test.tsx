import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Logo, Wordmark } from './Logo';

const ARCS = ['M3 12.5 Q12 23 21 12.5', 'M6.5 14 Q12 19.5 17.5 14'];

describe('the logo', () => {
  it('draws the nest: two arcs and the egg', () => {
    const { container, rerender } = render(<Logo />);
    expect([...container.querySelectorAll('path')].map((p) => p.getAttribute('d'))).toEqual(ARCS);
    const egg = container.querySelector('ellipse')!;
    expect(['cx', 'cy', 'rx', 'ry'].map((a) => egg.getAttribute(a))).toEqual(['12', '9', '3', '3.8']);
    expect(egg).toHaveAttribute('fill', 'var(--primary)');
    rerender(<Logo onTile />);
    expect(container.querySelector('ellipse')).toHaveAttribute('fill', 'currentColor');
  });

  it('paints with currentColor so forced colours repaint it', () => {
    const { container } = render(<Logo />);
    container.querySelectorAll('path').forEach((p) => expect(p).toHaveAttribute('stroke', 'currentColor'));
  });

  it('the wordmark is named « Nid » for assistive technology', () => {
    const { container, rerender } = render(<Wordmark />);
    expect(screen.getByText('Nid')).toHaveClass('sr-only');
    expect(container.querySelector('[data-wordmark]')).toHaveAttribute('aria-hidden', 'true');
    // The letters are a mask over the text colour (public/wordmark.svg), not paths in the bundle.
    const letters = container.querySelector('[data-wordmark]')!;
    expect(letters.tagName).toBe('SPAN');
    expect(letters).toHaveClass('bg-current', 'mask-[url(/wordmark.svg)]', 'mask-contain', 'forced-colors:bg-[CanvasText]', 'forced-colors:forced-color-adjust-none');
    expect(container.querySelectorAll('path')).toHaveLength(2);
    rerender(<Wordmark markOnly />);
    expect(container.querySelector('[data-wordmark]')).toBeNull();
    expect(screen.getByText('Nid')).toHaveClass('sr-only');
  });
});
