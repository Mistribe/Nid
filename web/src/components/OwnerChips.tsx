import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { personColor } from '@/features/dashboard/colors';
import { percent } from '@/features/dashboard/format';
import { PERSONS } from '@/features/subscriptions/api';

export type ItemOwner = { personId: string; shareBp: number };

/**
 * Who owns an item, on its list row: one round initial per person in their colour, the name for assistive technology
 * (and on hover), the share after the chips when it is split. The colour alone never says who (frontend/README.md §2,
 * principle 6). The colour rank is the dashboard's: adults by creation, then dependents (the persons list comes in
 * creation order). A child is shown by their first name.
 */
export function OwnerChips({ owners }: { owners: ItemOwner[] }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const people = $api.useQuery('get', PERSONS);
  if (!people.data || owners.length === 0) return null;

  const ranked = [...people.data].sort((a, b) => Number(a.kind !== 'adult') - Number(b.kind !== 'adult'));
  const shown = owners.flatMap((o) => {
    const slot = ranked.findIndex((p) => p.id === o.personId);
    const person = ranked[slot];
    return person ? [{ ...o, slot, name: person.kind === 'adult' ? person.displayName : person.displayName.split(' ')[0]! }] : [];
  });
  if (shown.length === 0) return null;

  // Equal shares read once (« 50 % »), unequal ones in the chips' order (« 70 % / 30 % »).
  const shares = shown.map((o) => t('owner.pct', { value: percent(o.shareBp / 100) }));
  const equal = new Set(shares).size === 1;
  const split = shown.length > 1 ? (equal ? shares[0] : shares.join(' / ')) : null;

  return (
    <span className="inline-flex items-center gap-2 text-sm text-muted-foreground">
      <span className="sr-only">{t('owner.label', { count: shown.length })}</span>
      <span className="inline-flex -space-x-1.5">
        {shown.map((o, i) => (
          <span key={o.personId} title={o.name} style={{ background: personColor(o.slot) }}
            className="inline-flex size-6 items-center justify-center rounded-full text-xs font-medium text-white ring-2 ring-background">
            <span aria-hidden="true">{[...o.name][0]?.toLocaleUpperCase('fr')}</span>
            <span className="sr-only">{equal ? o.name : `${o.name} ${shares[i]}`}</span>
          </span>
        ))}
      </span>
      {/* Unequal shares are read once per person, in the chip's name. */}
      {split ? <span aria-hidden={!equal || undefined} className="tabular-nums">{split}</span> : null}
    </span>
  );
}
