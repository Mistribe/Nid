/** A list row, or the row of a group of several: its parts and the server's group, the leading part's fields for its key. */
export type Folded<T, G> = T & { parts?: T[]; group?: G };

/** #794: the rows of the same owners fold into one row, in the server's groups' order; a group of one stays a plain row. */
export function foldByOwners<T extends { id: string }, G extends { items: string[] }>(items: T[], groups: G[]): Folded<T, G>[] {
  const byId = new Map(items.map((i) => [i.id, i]));
  return groups.flatMap((g) => {
    const parts = g.items.flatMap((id) => byId.get(id) ?? []);
    return parts.length > 1 ? [{ ...parts[0]!, parts, group: g }] : parts;
  });
}
