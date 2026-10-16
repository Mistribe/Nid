import type { Preferences } from '@/app/session';

export type Guide = {
  /** The section without its slash. */
  id: string;
  section: string;
  version: number;
  /** Keys guides.<id>.screens.<1..n>.title / .body. */
  screens: number;
  /** Keys guides.<id>.tour.<target>.title / .body, in order. */
  tour: readonly string[];
  /** A's last screen adds guides.<id>.hint when the page has no target to point at. */
  hint: boolean;
};

const guide = (id: string, screens: number, tour: readonly string[] = [], hint = false): Guide =>
  ({ id, section: `/${id}`, version: 1, screens, tour, hint });

export const GUIDES: readonly Guide[] = [
  guide('dashboard', 4, ['tiles', 'curve', 'split', 'upcoming', 'todo', 'scope']),
  guide('subscriptions', 3),
  guide('investments', 4, ['figures', 'evolution', 'enter', 'history'], true),
  guide('income', 3),
  guide('properties', 3),
  guide('loans', 4, ['figures', 'simulation', 'bank', 'schedule'], true),
  guide('routine', 3, ['due', 'save', 'estate']),
  guide('import', 3),
  guide('export', 3),
];

export const guideFor = (section: string) => GUIDES.find((g) => g.section === section);

export const isUnseen = (g: Guide, seen: Preferences['guidesSeen']) => (seen[g.id] ?? 0) < g.version;

export const allGuidesSeen = (): Preferences['guidesSeen'] => Object.fromEntries(GUIDES.map((g) => [g.id, g.version]));

/** The value of a target's data-guide attribute. */
export const targetOf = (g: Guide, target: string) => `${g.id}.${target}`;
