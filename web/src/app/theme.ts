import { defaultTheme, isMode, isThemeId, type Mode, type ThemeId } from '@/styles/themes';

const THEME_KEY = 'nid.theme';
const MODE_KEY = 'nid.mode';

/** A theme id this build no longer ships falls back to the default (design-system.md §7.6). */
export const resolveTheme = (id: string | null | undefined): ThemeId => (isThemeId(id) ? id : defaultTheme);
export const resolveMode = (mode: string | null | undefined): Mode => (isMode(mode) ? mode : 'system');

/** Switching theme is one attribute on <html>: no reload, nothing per component (design-system.md §7.1). */
export function applyTheme(
  theme: ThemeId,
  mode: Mode,
  root: HTMLElement = document.documentElement,
  media: MediaQueryList = window.matchMedia('(prefers-color-scheme: dark)'),
): void {
  root.dataset.theme = theme;
  root.classList.toggle('dark', mode === 'dark' || (mode === 'system' && media.matches));
  const background = getComputedStyle(root).getPropertyValue('--background').trim();
  if (background) document.querySelector('meta[name="theme-color"]')?.setAttribute('content', background);
}

/** The copy theme-init.js reads before the next first render; the server's preference stays the authority. */
export function rememberTheme(theme: ThemeId, mode: Mode): void {
  try {
    localStorage.setItem(THEME_KEY, theme);
    localStorage.setItem(MODE_KEY, mode);
  } catch {
    // Storage refused (private browsing): the next load flashes the default theme, nothing breaks.
  }
}
