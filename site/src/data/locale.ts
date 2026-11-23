export type Lang = 'fr' | 'en';

/** A site path in the given language: French at the root, English under /en/. */
export const localePath = (lang: Lang, path: string): string => (lang === 'en' ? `/en${path}` : path);
