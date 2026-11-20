import type { Option } from '@/components/form/options';
import type { BankCategory } from '@/api/banking';

/** A child is named with its parent (« Alimentation › Courses »). */
export const fullName = (all: BankCategory[], c: BankCategory) => { const p = all.find((x) => x.id === c.parentId); return p ? `${p.name} › ${c.name}` : c.name; };

/**
 * What `c` may merge into: same kind, in use, neither itself nor its own children; one with children merges only into a top-level one
 * (`banking.category_depth`).
 */
export function mergeTargets(all: BankCategory[], c: BankCategory): Option[] {
  const kept = all.filter((x) => !x.mergedIntoId);
  const parent = kept.some((x) => x.parentId === c.id);
  return kept.filter((x) => !x.archived && x.kind === c.kind && x.id !== c.id && x.parentId !== c.id && (!parent || !x.parentId))
    .map((x) => ({ value: x.id, label: fullName(kept, x) }));
}
