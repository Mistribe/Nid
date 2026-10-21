import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { cn } from '@/lib/utils';
import type { IncomeChange, IncomePhase } from '../api';
import { cadenceText } from '../format';
import { Change } from '../overview/OverviewFigures';
import '@/locales/income';

/**
 * Income dashboard §4: the source's dated steps as the server lists them, newest first — its start, each amount change
 * (before → after, percent), each new component, each end; upcoming ones dashed. A change without a percent changed its
 * cadence too: both cadences show next to the amounts; a start or a new component shows its own (a yearly bonus is not
 * monthly). A phase's `⋯` menu sits on the row that starts it (an end row names a phase that already has one).
 */
export function ChangesTimeline({ changes, phases, actions }: {
  changes: IncomeChange[]; phases: IncomePhase[]; actions?: (phase: IncomePhase) => ReactNode;
}) {
  const { t } = useTranslation();
  return (
    <ol aria-label={t('income_detail.timeline.list')} className="space-y-1">
      {changes.map((c) => {
        const phase = phases.find((p) => p.id === c.phaseId);
        // The phase this change follows: the latest of its label starting before it.
        const previous = phases.filter((p) => p.label === c.label && p.startsOn < c.on).sort((a, b) => (a.startsOn < b.startsOn ? 1 : -1))[0];
        const cadences = Boolean(c.kind === 'change' && c.percent == null && phase && previous);
        const own = cadences || c.kind === 'start' || c.kind === 'added';
        const every = (p: IncomePhase | undefined, shown: boolean) => (shown && p ? <span className="text-sm text-muted-foreground">({cadenceText(t, p.every).toLocaleLowerCase('fr')})</span> : null);
        // A kind the screen does not know reads as its raw name.
        const kind = c.kind === 'start' || c.kind === 'added' || c.kind === 'ended' ? t(`income_detail.timeline.${c.kind}`) : c.kind === 'change' ? null : c.kind;
        return (
          <li key={`${c.kind}-${c.phaseId}`} className="flex min-h-9 items-center gap-3">
            <span aria-hidden="true" className={cn('size-2.5 shrink-0 rounded-full border-2 border-primary', c.upcoming ? 'border-dashed' : 'bg-primary')} />
            <span className="flex flex-1 flex-wrap items-center gap-x-1.5">
              <LocalDate value={c.on} /><span aria-hidden="true">·</span>
              <span className="font-medium">{c.label}</span><span aria-hidden="true">·</span>
              {kind ? <><span>{kind}</span><span aria-hidden="true">·</span></> : null}
              {c.before ? <Money value={c.before} tabular /> : null}{every(previous, cadences)}
              {c.before && c.after ? <><span aria-hidden="true">→</span><span className="sr-only">{t('income_detail.timeline.to')}</span></> : null}
              {c.after ? <Money value={c.after} tabular /> : null}{every(phase, own)}
              {c.percent != null ? <Change value={c.percent} /> : null}
              {c.upcoming ? <span className="text-sm text-muted-foreground">({t('income_detail.timeline.upcoming')})</span> : null}
            </span>
            {phase && actions && c.kind !== 'ended' ? actions(phase) : null}
          </li>
        );
      })}
    </ol>
  );
}
