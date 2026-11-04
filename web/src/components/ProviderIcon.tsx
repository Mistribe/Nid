import { useState } from 'react';
import { labelColour } from '@/lib/labelColour';
import { cn } from '@/lib/utils';

/** The server's neutral label colour (`CatalogLimits.NeutralColor`): a chart fallback for the other features (a monogram takes its chip from `labelColour`). */
export const NEUTRAL_COLOR = '#9E9E9E';

/** The first character of the name's first two words, upper-cased: "Apple Music" → "AM", "Netflix" → "N". */
export function initials(name: string): string {
  return name
    .trim()
    .split(/\s+/u)
    .filter((word) => word.length > 0)
    .slice(0, 2)
    .map((word) => (Array.from(word)[0] ?? '').toLocaleUpperCase('fr'))
    .join('');
}

type Props = {
  name: string;
  /** The server's `iconUrl`, used as given: never built here (1C2c design §7). */
  iconUrl?: string | null;
  /** The colour of the provider's first label (`#RRGGBB`): the chip takes the nearest chart colour. */
  color?: string | null;
  className?: string;
};

/**
 * A provider's icon, served by Nid's own origin, or its monogram — initials on a chip tinted by its first label — when
 * it has none or the image fails to load (1C2c design §7). Decorative: the name is always shown beside it.
 */
export function ProviderIcon({ name, iconUrl, color, className }: Props) {
  const [failed, setFailed] = useState<string | null>(null);
  const box = cn('inline-flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-md', className);

  if (iconUrl && failed !== iconUrl)
    return <img src={iconUrl} alt="" width={32} height={32} className={cn(box, 'object-contain')} onError={() => setFailed(iconUrl)} />;

  return (
    <span
      aria-hidden="true"
      data-testid="provider-monogram"
      className={cn(box, 'text-xs font-semibold text-foreground')}
      style={{ background: `color-mix(in oklab, ${labelColour(color)} 22%, var(--card))` }}
    >
      {initials(name)}
    </span>
  );
}
