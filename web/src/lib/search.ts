/** Foyer is the default and stays out of the URL; Moi is `?scope=me` (screens.md §1). */
export type RootSearch = { scope?: 'me' };

/** The list `page` of the URL: a whole number from 2 (page 1 is the default and stays out of it), else dropped. */
export const pageOf = (search: Record<string, unknown>): number | undefined => {
  const page = Number(search.page);
  return Number.isInteger(page) && page >= 2 ? page : undefined;
};
