// Adding a theme = one CSS file imported in globals.css + one entry here (design-system.md §7.4).
export const themes = [
  { id: 'sobre', swatches: ['oklch(1 0 0)', 'oklch(0.205 0 0)', 'oklch(0.97 0 0)'] },
  { id: 'ardoise', swatches: ['oklch(0.984 0.003 247.9)', 'oklch(0.546 0.215 262.9)', 'oklch(0.929 0.013 255.5)'] },
  { id: 'sauge', swatches: ['oklch(0.985 0.006 140)', 'oklch(0.45 0.07 150)', 'oklch(0.95 0.015 145)'] },
  { id: 'terre', swatches: ['oklch(0.985 0.008 75)', 'oklch(0.5 0.12 45)', 'oklch(0.95 0.015 70)'] },
] as const;

export type ThemeId = (typeof themes)[number]['id'];
export const defaultTheme: ThemeId = 'ardoise';

export const modes = ['system', 'light', 'dark'] as const;
export type Mode = (typeof modes)[number];

export const isThemeId = (value: unknown): value is ThemeId => themes.some((t) => t.id === value);
export const isMode = (value: unknown): value is Mode => modes.some((m) => m === value);
