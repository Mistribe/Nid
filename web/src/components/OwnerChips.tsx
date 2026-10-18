import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { personColor } from '@/features/dashboard/colors';
import { percent } from '@/features/dashboard/format';
import { PERSONS } from '@/features/subscriptions/api';

export type ItemOwner = { personId: string; shareBp: number };

/**
 * Who owns an item, on its list row: a dot in the person's colour and their name, with the share when it is split.
 * The name always says it: the colour alone never does (frontend/README.md §2, principle 6). The colour rank is the
 * dashboard's: adults by creation, then dependents (the persons list comes in creation order).
 */
export function OwnerChips({ owners }: { owners: ItemOwner[] }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const people = $api.useQuery('get', PERSONS);
  if (!people.data || owners.length === 0) return null;

  const ranked = [...people.data].sort((a, b) => Number(a.kind !== 'adult') - Number(b.kind !== 'adult'));
  const shown = owners.flatMap((o) => {
    const slot = ranked.findIndex((p) => p.id === o.personId);
    return slot < 0 ? [] : [{ ...o, name: ranked[slot]!.displayName, slot }];
  });
  if (shown.length === 0) return null;

  return (
    <span className="inline-flex flex-wrap items-center gap-x-3 text-sm text-muted-foreground">
      <span className="sr-only">{t('owner.label', { count: shown.length })}</span>
      {shown.map((o) => (
        <span key={o.personId} className="inline-flex items-center gap-1">
          <span aria-hidden="true" className="inline-block size-3 rounded-sm" style={{ background: personColor(o.slot) }} />
          {o.name}
          {shown.length > 1 ? <span className="tabular-nums">{t('owner.pct', { value: percent(o.shareBp / 100) })}</span> : null}
        </span>
      ))}
    </span>
  );
}
