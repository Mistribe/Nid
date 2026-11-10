import { useTranslation } from 'react-i18next';
import { Toggle } from '@/components/ui/toggle';
import type { Projection, ProjectionRanges, RangePoint } from '@/api/dashboard';
import { CLASSES, useClassName } from './panels/allocationNames';

/** « Fourchette » (ranges spec §6): asks the server for the bands; they cost a second or so, so they are never asked by default. */
export function RangesToggle({ pressed, onPressedChange }: { pressed: boolean; onPressedChange: (pressed: boolean) => void }) {
  const { t } = useTranslation();
  return (
    <Toggle variant="outline" size="sm" pressed={pressed} onPressedChange={onPressedChange}>
      {t('dashboard.projection.ranges')}
    </Toggle>
  );
}

/** A band in the chosen reading: the server's strings, nothing deflated here. */
export const bandOf = (p: RangePoint, real: boolean) => (real
  ? { low: p.lowReal, median: p.medianReal, high: p.highReal }
  : { low: p.low, median: p.median, high: p.high });

/** A server percent as a French one: « 6.0 » reads « 6,0 ». */
const comma = (value: string) => value.replace('.', ',');

/**
 * Ranges spec §6: the share of runs whose savings last to the horizon, then what the bands rest on — the runs, one market factor, each
 * class's volatility, what does not vary.
 */
export function RangesSummary({ ranges, assumptions }: { ranges: ProjectionRanges; assumptions: Projection['assumptions'] }) {
  const { t } = useTranslation();
  const className = useClassName();
  const runs = ranges.runs.toLocaleString('fr-FR');
  const volatile = CLASSES.filter((c) => !/^0+([.,]0+)?$/.test(assumptions.classVolatility[c] ?? '0'))
    .map((c) => t('dashboard.projection.ranges_class', { name: className(c), percent: comma(assumptions.classVolatility[c] ?? '0') }));
  return (
    <div className="space-y-1 text-sm">
      <p className="font-medium">{t('dashboard.projection.ranges_success', { percent: ranges.success, runs })}</p>
      <p className="text-muted-foreground">
        {t('dashboard.projection.ranges_model', { runs, volatility: volatile.length > 0 ? volatile.join(', ') : t('dashboard.projection.ranges_none') })}
      </p>
    </div>
  );
}
