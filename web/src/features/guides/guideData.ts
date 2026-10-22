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

const guide = (id: string, screens: number, tour: readonly string[] = [], hint = false, version = 1): Guide =>
  ({ id, section: `/${id}`, version, screens, tour, hint });

export const GUIDES: readonly Guide[] = [
  guide('dashboard', 4, ['tiles', 'month', 'curve', 'upcoming', 'scope'], false, 2),
  guide('subscriptions', 4, [], false, 3),
  guide('investments', 5, ['figures', 'evolution', 'enter', 'history', 'holdings'], true, 4),
  guide('income', 3),
  guide('properties', 3, [], false, 2),
  guide('loans', 4, ['figures', 'simulation', 'schedule'], true, 3),
  guide('routine', 3, ['due', 'save', 'estate']),
  guide('import', 3),
  guide('export', 3),
];

export const guideFor = (section: string) => GUIDES.find((g) => g.section === section);

export const isUnseen = (g: Guide, seen: Preferences['guidesSeen']) => (seen[g.id] ?? 0) < g.version;

export const allGuidesSeen = (): Preferences['guidesSeen'] => Object.fromEntries(GUIDES.map((g) => [g.id, g.version]));

/** The value of a target's data-guide attribute. */
export const targetOf = (g: Guide, target: string) => `${g.id}.${target}`;

/** A tour step's element in the page, if it is there now. */
export const findTarget = (g: Guide, target: string) => document.querySelector<HTMLElement>(`[data-guide="${targetOf(g, target)}"]`);
