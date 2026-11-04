import { render } from '@testing-library/react';
import { PiggyBank } from 'lucide-react';
import { describe, expect, it } from 'vitest';
import { ModuleTile } from './ModuleTile';

const tile = (props: Partial<Parameters<typeof ModuleTile>[0]> = {}) =>
  render(<ModuleTile icon={PiggyBank} tile={3} {...props} />).container.querySelector<HTMLElement>('[data-tile]')!;

describe('ModuleTile', () => {
  it('paints the module colour behind a white glyph', () => {
    const el = tile();
    expect(el.style.background).toBe('var(--tile-3)');
    expect(el).toHaveClass('text-white');
    expect(el.querySelector('svg')).toHaveAttribute('stroke', 'currentColor');
  });

  // The command menu's `[cmdk-item] svg` rules (size-5, a grey `svg:not([class*=text-])`) outrank a plain class.
  it('keeps its glyph size and colour inside the command menu', () => {
    expect(tile().querySelector('svg')).toHaveClass('text-current', 'size-[62%]!');
  });

  it('a primary tile takes the theme accent', () => {
    const el = tile({ tile: 'primary' });
    expect(el.style.background).toBe('var(--primary)');
    expect(el.style.color).toBe('var(--primary-foreground)');
  });

  it('is decoration', () => {
    expect(tile()).toHaveAttribute('aria-hidden', 'true');
  });

  it.each([['sm', 'size-[22px]'], ['lg', 'size-12']] as const)('%s size', (size, cls) => {
    expect(tile({ size })).toHaveClass(cls);
  });
});
