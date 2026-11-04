export type LabelSlot = 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 'neutral';

// The light palette of --chart-1…8 (dataviz.md §3), as hex; `SLOTS` below holds the same colours in OKLab.
export const PALETTE = ['#3679cd', '#e46e42', '#37ad7d', '#e8a332', '#e280a4', '#1f811c', '#4a3e9f', '#db534f'];

const RGB = /^#[0-9a-f]{6}$/i;

/** Below this OKLCH chroma a colour is a grey: it takes no hue. */
const GREY_CHROMA = 0.04;

function oklab(hex: string): [number, number, number] {
  const [r, g, b] = [1, 3, 5].map((at) => {
    const c = parseInt(hex.slice(at, at + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  return [
    0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
    1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
    0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s,
  ];
}

const SLOTS = PALETTE.map(oklab);

/** The palette slot nearest (OKLab) to a stored label colour (`#RRGGBB`), or neutral for a grey or an unusable value. */
export function labelSlot(hex: string | null | undefined): LabelSlot {
  if (!hex || !RGB.test(hex)) return 'neutral';
  const [l, a, b] = oklab(hex);
  if (Math.hypot(a, b) < GREY_CHROMA) return 'neutral';
  const distances = SLOTS.map(([sl, sa, sb]) => Math.hypot(l - sl, a - sa, b - sb));
  return (distances.indexOf(Math.min(...distances)) + 1) as LabelSlot;
}

/** The CSS colour of a label: its nearest `--chart-N`, or the muted foreground for a grey. */
export function labelColour(hex: string | null | undefined): string {
  const slot = labelSlot(hex);
  return slot === 'neutral' ? 'var(--muted-foreground)' : `var(--chart-${slot})`;
}
