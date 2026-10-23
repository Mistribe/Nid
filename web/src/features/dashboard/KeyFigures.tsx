import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { cn } from '@/lib/utils';
import { Direction } from '../investments/Figures';
import { directionOf } from '../investments/format';
import type { DashboardFigure, DashboardView } from './api';
import { percent } from './format';
import { Sparkline } from './Sparkline';

export type Panel = 'net_worth' | 'income' | 'commitments' | 'margin';

/** The figure, or « Inconnu » with its reason (a missing rate names its currency). */
export function Value({ figure, unknown, unknownRate = 'dashboard.unknown_rate', className = 'text-2xl' }: {
  figure: DashboardFigure; unknown: string; unknownRate?: 'dashboard.unknown_rate' | 'dashboard.change_unknown_rate'; className?: string;
}) {
  const { t } = useTranslation();
  if (figure.total) return <span className={cn('font-semibold tracking-tight', className)}>{formatMoney(figure.total)}</span>;
  return <span className="text-sm text-muted-foreground">{figure.missingCurrency ? t(unknownRate, { code: figure.missingCurrency }) : unknown}</span>;
}

function Tile({ label, panel, onOpen, className, children }: { label: string; panel: Panel; onOpen: (p: Panel) => void; className?: string; children: ReactNode }) {
  return (
    <button type="button" onClick={() => onOpen(panel)}
      className={cn('flex min-w-0 flex-col items-start gap-1 rounded-xl border bg-card p-3 text-left shadow-card outline-none transition-[translate,box-shadow,border-color] duration-200 ease-emphasized hover:-translate-y-0.5 hover:border-primary/30 hover:shadow-raised focus-visible:ring-[3px] focus-visible:ring-ring/50', className)}>
      <span className="text-sm text-muted-foreground">{label}</span>
      {children}
    </button>
  );
}

const CHANGE_TONE = { up: 'bg-status-good/10', down: 'bg-status-critical/10', flat: 'bg-muted' } as const;

/**
 * Spec §4.1: the net worth first and large, with its 12-month line, then three compact figures; each opens its panel.
 * On a phone: net worth alone, then income and margin side by side, then commitments.
 */
export function KeyFigures({ d, onOpen, scope }: { d: DashboardView; onOpen: (panel: Panel) => void; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const missing = d.income.missingCurrency ?? d.commitments.missingCurrency;
  const change = d.change.total;
  return (
    <div data-guide="dashboard.tiles" className="stagger grid grid-cols-2 gap-3 md:grid-cols-3">
      <Tile label={t('dashboard.key.net_worth')} panel="net_worth" onOpen={onOpen}
        className="relative col-span-2 gap-2 bg-linear-to-br from-primary/10 via-card to-card p-5 sm:pr-72 md:col-span-3">
        <span>
          <Value figure={d.netWorth} unknown={t('dashboard.net_worth_unknown')} className="text-3xl tracking-tight md:text-4xl" />
          {d.netWorth.total && d.estimatedEnvelopes > 0 ? <span className="text-sm text-muted-foreground"> {t('dashboard.estimated_total')}</span> : null}
        </span>
        {change ? (
          <span className={cn('rounded-full px-2.5 py-0.5 text-sm', CHANGE_TONE[directionOf(change.amount)])}>
            <Direction amount={change}>
              {d.changePct != null ? <span className="tabular-nums">{t('dashboard.pct', { value: percent(d.changePct, true) })}</span> : null}
            </Direction>
          </span>
        ) : <span className="text-sm text-muted-foreground"><Value figure={d.change} unknown={t('dashboard.change_unknown')} unknownRate="dashboard.change_unknown_rate" /></span>}
        {d.envelopes > 0 ? <span className="absolute right-5 bottom-5 hidden w-56 sm:block"><Sparkline scope={scope} /></span> : null}
      </Tile>
      <Tile label={t('dashboard.key.income')} panel="income" onOpen={onOpen}>
        <Value figure={d.income} unknown={t('dashboard.income_unknown')} />
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
