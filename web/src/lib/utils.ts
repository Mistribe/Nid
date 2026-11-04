import { clsx, type ClassValue } from 'clsx';
import { extendTailwindMerge } from 'tailwind-merge';

// The type scale (`text-figure`…) and `rounded-pane` are theme tokens: without this they read as a colour / unknown.
// The layers (globals.css) paint a background colour: a later `bg-…` replaces them, and the reverse.
const twMerge = extendTailwindMerge({ extend: { classGroups: {
  'font-size': [{ text: ['large-title', 'title', 'hero', 'figure', 'label'] }],
  rounded: [{ rounded: ['pane'] }],
  'bg-color': ['material', 'glass', 'glass-regular', 'content-pane'],
} } });

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
