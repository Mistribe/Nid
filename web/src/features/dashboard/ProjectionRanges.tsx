import { useTranslation } from 'react-i18next';
import { InfoTip } from '@/components/InfoTip';
import { formatCurrency } from '@/components/Money';
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
 * Ranges spec §6, in plain words (issue #161): where the net worth lands at the horizon in 9 cases out of 10 and the share of runs
 * whose savings last, the server's figures; how the bands are built (the runs, one market factor, each class's volatility, what
 * does not vary) sits in an InfoTip.
 */
export function RangesSummary({ ranges, assumptions, real, currency }: {
  ranges: ProjectionRanges; assumptions: Projection['assumptions']; real: boolean; currency: string;
}) {
  const { t } = useTranslation();
  const className = useClassName();
  const runs = ranges.runs.toLocaleString('fr-FR');
  const volatile = CLASSES.filter((c) => !/^0+([.,]0+)?$/.test(assumptions.classVolatility[c] ?? '0'))
    .map((c) => t('dashboard.projection.ranges_class', { name: className(c), percent: comma(assumptions.classVolatility[c] ?? '0') }));
  const horizon = ranges.points.at(-1);
  const band = horizon ? bandOf(horizon, real) : null;
  const whole = (amount: string) => formatCurrency(amount, currency, { maximumFractionDigits: 0 });
  const how = t('dashboard.projection.ranges_how');
  return (
    <div className="space-y-1 text-sm">
      {horizon && band ? (
        <p className="flex items-center gap-1.5 font-medium">
          <span>{t('dashboard.projection.ranges_plain', { low: whole(band.low), high: whole(band.high), year: horizon.on.slice(0, 4) })}</span>
          <InfoTip label={how}>
            {t('dashboard.projection.ranges_model', { runs, volatility: volatile.length > 0 ? volatile.join(', ') : t('dashboard.projection.ranges_none') })}
          </InfoTip>
        </p>
      ) : null}
      <p className="text-muted-foreground">{t('dashboard.projection.ranges_success', { percent: ranges.success })}</p>
    </div>
  );
}
