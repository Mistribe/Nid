import { useId, type ReactNode } from 'react';
import type { MoneyValue } from '@/components/Money';
import { Direction } from '@/features/investments/Figures';
import { SparkPath } from './SparkPath';

/**
 * The hero's inside (visual identity spec): the label, the figure large, its change, a note, and the curve on the right from
 * `sm` up. Spans only, so the dashboard's net-worth tile, a button, holds it too.
 */
export function HeroBody({ label, labelId, figure, change, note, curve }: {
  label: string; labelId?: string; figure: ReactNode; change?: ReactNode; note?: ReactNode; curve?: ReactNode;
}) {
  return (
    <span className="flex w-full items-end justify-between gap-6">
      <span className="flex min-w-0 flex-col items-start gap-2">
        <span id={labelId} className="text-label text-muted-foreground">{label}</span>
        <span className="text-hero tabular-nums">{figure}</span>
        {change ? <span className="text-sm">{change}</span> : null}
        {note ? <span className="text-sm text-muted-foreground">{note}</span> : null}
      </span>
      {curve}
    </span>
  );
}

/** The curve's box on the right from `sm` up, only with a curve to draw: no empty box is left beside the figure. */
export function HeroCurve({ values }: { values: number[] }) {
  return values.length > 1 ? <span className="hidden w-56 shrink-0 sm:block"><SparkPath values={values} /></span> : null;
}

/** An unknown figure (« Inconnu » and why) in the hero: small and set back, so it does not read as a data point. */
export function HeroUnknown({ children }: { children: ReactNode }) {
  return <span className="text-sm font-normal tracking-normal text-muted-foreground">{children}</span>;
}

/** The server's signed change with its arrow, the server's percent set back after a rule, then since when. */
export function HeroChange({ amount, pct, children }: { amount: MoneyValue; pct?: string | null; children?: ReactNode }) {
  return (
    <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
      <span className="font-medium">
        <Direction amount={amount}>
          {pct ? (
            <>
              <span aria-hidden="true" className="mx-1.5 h-3.5 w-px bg-foreground/20" />
              <span className="tabular-nums font-normal text-muted-foreground">{pct}</span>
            </>
          ) : null}
        </Direction>
      </span>
      {children ? <span className="text-muted-foreground">{children}</span> : null}
    </span>
  );
}

/** An overview's lead figure; `values` are plotting numbers (`toPlot`) that only shape the curve. */
export function Hero({ label, figure, change, note, values }: {
  label: string; figure: ReactNode; change?: ReactNode; note?: ReactNode; values?: number[];
}) {
  const labelId = useId();
  return (
    <section aria-labelledby={labelId} className="material rounded-[22px] border p-5 shadow-card">
      <HeroBody label={label} labelId={labelId} figure={figure} change={change} note={note}
        curve={values ? <HeroCurve values={values} /> : null} />
    </section>
  );
}
