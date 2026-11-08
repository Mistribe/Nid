import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { HeroBody, HeroChange, HeroUnknown } from '@/components/page/Hero';
import { cn } from '@/lib/utils';
import type { DashboardFigure, DashboardView } from './api';
import { percent } from '@/lib/format';
import { Sparkline } from './Sparkline';

export type Panel = 'net_worth' | 'income' | 'commitments' | 'margin';

/** The figure, or « Inconnu » with its reason (a missing rate names its currency). */
export function Value({ figure, unknown, unknownRate = 'dashboard.unknown_rate', className = 'text-2xl font-semibold tracking-tight' }: {
  figure: DashboardFigure; unknown: string; unknownRate?: 'dashboard.unknown_rate' | 'dashboard.change_unknown_rate'; className?: string;
}) {
  const { t } = useTranslation();
  if (figure.total) return <span className={className}>{formatMoney(figure.total)}</span>;
  return <HeroUnknown>{figure.missingCurrency ? t(unknownRate, { code: figure.missingCurrency }) : unknown}</HeroUnknown>;
}

function Tile({ label, panel, onOpen, className, children }: { label?: string; panel: Panel; onOpen: (p: Panel) => void; className?: string; children: ReactNode }) {
  return (
    <button type="button" onClick={() => onOpen(panel)}
      className={cn('flex min-w-0 flex-col items-start gap-1 rounded-xl border material p-3 text-left shadow-card outline-none transition-[translate,box-shadow,border-color] duration-200 ease-emphasized hover:-translate-y-0.5 hover:border-primary/30 hover:shadow-raised focus-visible:ring-[3px] focus-visible:ring-ring/50', className)}>
      {label ? <span className="text-label text-muted-foreground">{label}</span> : null}
      {children}
    </button>
  );
}

/**
 * Spec §4.1: the net worth first and large, with its 12-month line, then three compact figures; each opens its panel.
 * No period control (1M, 6M, 1A, Max): it would need data the server does not give (spec non-goal: no new data).
 * On a phone: net worth alone, then income and margin side by side, then commitments.
 */
export function KeyFigures({ d, onOpen, scope }: { d: DashboardView; onOpen: (panel: Panel) => void; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const missing = d.income.missingCurrency ?? d.commitments.missingCurrency;
  const change = d.change.total;
  return (
    <div data-guide="dashboard.tiles" className="stagger grid grid-cols-2 gap-3 md:grid-cols-3">
      <Tile panel="net_worth" onOpen={onOpen} className="col-span-2 rounded-[22px] p-5 md:col-span-3">
        <HeroBody label={t('dashboard.key.net_worth')}
          figure={(
            <>
              <Value figure={d.netWorth} unknown={t('dashboard.net_worth_unknown')} className="text-hero tabular-nums" />
              {d.netWorth.total && d.estimatedEnvelopes > 0 ? <span className="text-sm font-normal tracking-normal text-muted-foreground"> {t('dashboard.estimated_total')}</span> : null}
            </>
          )}
          // A change says since when, and what it leaves out: an item without a recent value on 1 January has no start.
          change={change
            ? <HeroChange amount={change} pct={d.changePct != null ? t('dashboard.pct', { value: percent(d.changePct, true) }) : null}>{t('dashboard.since_january')}</HeroChange>
            : <Value figure={d.change} unknown={t('dashboard.change_unknown')} unknownRate="dashboard.change_unknown_rate" />}
          note={change && d.envelopesWithoutStart > 0 ? t('dashboard.without_start', { count: d.envelopesWithoutStart }) : null}
          curve={d.envelopes > 0 ? <Sparkline scope={scope} /> : null} />
      </Tile>
      <Tile label={t('dashboard.key.income')} panel="income" onOpen={onOpen}>
        <Value figure={d.income} unknown={t('dashboard.income_unknown')} />
        {d.income.total ? <span className="text-xs text-muted-foreground">{t('dashboard.key.income_hint')}</span> : null}
      </Tile>
      <Tile label={t('dashboard.key.margin')} panel="margin" onOpen={onOpen} className="md:order-4">
        {d.margin ? (
          <>
            <span className="text-2xl font-semibold tracking-tight">{formatMoney(d.margin)}</span>
            <span className="text-xs text-muted-foreground">{t('dashboard.key.margin_hint')}</span>
          </>
        ) : (
          <span className="text-sm text-muted-foreground">
            {d.incomeSources > 0 && missing ? t('dashboard.margin_unknown_rate', { code: missing }) : t('dashboard.margin_unknown')}
          </span>
        )}
      </Tile>
      <Tile label={t('dashboard.key.commitments')} panel="commitments" onOpen={onOpen} className="col-span-2 md:order-3 md:col-span-1">
        {/* A source the server lists counts items: with nothing due this month it is « aucun coût », not « aucun engagement ». */}
        <Value figure={d.commitments} unknown={t(d.commitmentSources.length > 0 ? 'dashboard.commitments_none_this_month' : 'dashboard.commitments_none')} />
        {d.committedPct != null ? (
          <>
            {/* The server's share sizes the bar; the text beside it says the figure. */}
            <span aria-hidden="true" className="mt-1 h-1 w-full overflow-hidden rounded-full bg-muted">
              <span className="block h-full origin-left animate-grow rounded-full bg-chart-4" style={{ width: `${Math.min(d.committedPct, 100)}%` }} />
            </span>
            <span className="text-xs text-muted-foreground">{t('dashboard.committed_share', { value: percent(d.committedPct) })}</span>
          </>
        ) : null}
      </Tile>
    </div>
  );
}
