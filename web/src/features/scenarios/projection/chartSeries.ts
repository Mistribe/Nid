import type { TFunction } from 'i18next';
import type { HouseholdPoint } from '@/features/dashboard/api';
import type { ProjectionComparison } from './api';
import '@/locales/scenarios';

/** One line of the projection chart: its CSS-safe key (`--color-{key}`), its legend label and the server's points. */
export type ChartSeries = { key: string; label: string; points: HouseholdPoint[] };

/** The detail page's two lines: the base, then the scenario. Apart from the chart, which pages load lazily. */
export const baseAndScenario = (comparison: ProjectionComparison, t: TFunction): ChartSeries[] => [
  { key: 'base', label: t('scenarios.projection.result.base'), points: comparison.base.points },
  { key: 'scenario', label: t('scenarios.projection.result.scenario'), points: comparison.scenario.points },
];
