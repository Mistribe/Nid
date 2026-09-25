import { useState } from 'react';
import { cn } from '@/lib/utils';

/** The server's neutral label colour (`CatalogLimits.NeutralColor`): the monogram's background when no label colour is usable. */
export const NEUTRAL_COLOR = '#9E9E9E';

const RGB = /^#[0-9a-f]{6}$/i;

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

function channel(color: string, at: number): number {
  const value = parseInt(color.slice(at, at + 2), 16) / 255;
  return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
}

/**
 * Black or white, whichever contrasts more with the background (WCAG 2 relative luminance). The better of the two
 * always reaches 4.58:1, so a monogram is readable on any label colour the catalog may bring.
 */
export function monogramInk(background: string): '#000000' | '#FFFFFF' {
  const color = RGB.test(background) ? background : NEUTRAL_COLOR;
  const luminance = 0.2126 * channel(color, 1) + 0.7152 * channel(color, 3) + 0.0722 * channel(color, 5);
  return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? '#000000' : '#FFFFFF';
}

type Props = {
  name: string;
  /** The server's `iconUrl`, used as given: never built here (1C2c design §7). */
  iconUrl?: string | null;
  /** The colour of the provider's first label (`#RRGGBB`). */
  color?: string | null;
  className?: string;
};

/**
 * A provider's icon, served by Nid's own origin, or its monogram — initials on the colour of its first label — when
 * it has none or the image fails to load (1C2c design §7). Decorative: the name is always shown beside it.
 */
export function ProviderIcon({ name, iconUrl, color, className }: Props) {
  const [failed, setFailed] = useState<string | null>(null);
  const box = cn('inline-flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-md', className);

  if (iconUrl && failed !== iconUrl)
    return <img src={iconUrl} alt="" width={32} height={32} className={cn(box, 'object-contain')} onError={() => setFailed(iconUrl)} />;

  const background = color && RGB.test(color) ? color : NEUTRAL_COLOR;
  return (
    <span
      aria-hidden="true"
      data-testid="provider-monogram"
      className={cn(box, 'text-xs font-semibold')}
      style={{ backgroundColor: background, color: monogramInk(background) }}
    >
      {initials(name)}
    </span>
  );
}
