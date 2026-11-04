import { describe, expect, it } from 'vitest';
import { cn } from './utils';

describe('cn', () => {
  it('keeps a type-scale size next to a text colour', () => {
    expect(cn('text-figure', 'text-status-good-text')).toBe('text-figure text-status-good-text');
  });
  it('lets a later size replace a type-scale size', () => {
    expect(cn('text-title', 'text-lg')).toBe('text-lg');
  });
  it('merges rounded-pane with another radius', () => {
    expect(cn('rounded-xl', 'rounded-pane')).toBe('rounded-pane');
  });
  it.each(['material', 'glass', 'glass-regular', 'content-pane'])('lets a background colour replace the %s layer, and the reverse', (layer) => {
    expect(cn(layer, 'bg-card')).toBe('bg-card');
    expect(cn('bg-card', layer)).toBe(layer);
  });
  it.each(['bg-fill', 'bg-separator'])('keeps %s next to other background utilities', (c) => {
    expect(cn(c, 'rounded-md', 'text-sm')).toBe(`${c} rounded-md text-sm`);
    expect(cn(c, 'bg-card')).toBe('bg-card');
  });
});
