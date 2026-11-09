import fr from '@/locales/fr.json';
import series from '@/locales/fr.series.json';

const fold = (s: string) => s.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
const classes = Object.values(fr.settings.projection.class);
// Each kind code with the French words that name it: its label, and for a projected rate the asset classes.
const LABELS = Object.entries(series.series.kind).map(([kind, label]) =>
  [kind, [label.replace('{{name}}', ''), ...(kind === 'projection_rate' ? classes : [])].map(fold)] as const);

/** The kind codes whose French label contains `text`, ignoring case and accents: « ipc » finds `cpi`, « loyer » `irl`. */
export const kindsMatching = (text: string): string[] => {
  const needle = fold(text.trim());
  return needle ? LABELS.filter(([, words]) => words.some((w) => w.includes(needle))).map(([kind]) => kind) : [];
};
