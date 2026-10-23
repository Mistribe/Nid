import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { cn } from '@/lib/utils';
import { Direction } from '../investments/Figures';
import type { DashboardFigure, DashboardView } from './api';
import { percent } from './format';

export type Panel = 'net_worth' | 'income' | 'commitments' | 'margin';

/** The figure, or « Inconnu » with its reason (a missing rate names its currency). */
export function Value({ figure, unknown, unknownRate = 'dashboard.unknown_rate' }: { figure: DashboardFigure; unknown: string; unknownRate?: 'dashboard.unknown_rate' | 'dashboard.change_unknown_rate' }) {
  const { t } = useTranslation();
  if (figure.total) return <span className="text-xl font-semibold tabular-nums">{formatMoney(figure.total)}</span>;
  return <span className="text-sm text-muted-foreground">{figure.missingCurrency ? t(unknownRate, { code: figure.missingCurrency }) : unknown}</span>;
}

function Tile({ label, panel, onOpen, className, children }: { label: string; panel: Panel; onOpen: (p: Panel) => void; className?: string; children: ReactNode }) {
  return (
    <button type="button" onClick={() => onOpen(panel)}
      className={cn('flex min-w-0 flex-col items-start gap-1 rounded-xl border bg-card p-3 text-left shadow-xs outline-none transition-colors hover:bg-accent/40 focus-visible:ring-[3px] focus-visible:ring-ring/50', className)}>
      <span className="text-sm text-muted-foreground">{label}</span>
      {children}
    </button>
  );
}

/** Spec §4.1: four compact figures in one row, each opening its panel. On a phone: net worth alone, then income and margin side by side. */
export function KeyFigures({ d, onOpen }: { d: DashboardView; onOpen: (panel: Panel) => void }) {
  const { t } = useTranslation();
  const missing = d.income.missingCurrency ?? d.commitments.missingCurrency;
  const change = d.change.total;
  return (
    <div data-guide="dashboard.tiles" className="grid grid-cols-2 gap-3 md:grid-cols-4">
      <Tile label={t('dashboard.key.net_worth')} panel="net_worth" onOpen={onOpen} className="col-span-2 md:col-span-1">
        <span>
          <Value figure={d.netWorth} unknown={t('dashboard.net_worth_unknown')} />
          {d.netWorth.total && d.estimatedEnvelopes > 0 ? <span className="text-xs text-muted-foreground"> {t('dashboard.estimated_total')}</span> : null}
        </span>
        <span className="text-sm">
          {change ? (
            <Direction amount={change}>
              {d.changePct != null ? <span className="tabular-nums">{t('dashboard.pct', { value: percent(d.changePct, true) })}</span> : null}
            </Direction>
          ) : <span className="text-muted-foreground"><Value figure={d.change} unknown={t('dashboard.change_unknown')} unknownRate="dashboard.change_unknown_rate" /></span>}
        </span>
      </Tile>
      <Tile label={t('dashboard.key.income')} panel="income" onOpen={onOpen}>
        <Value figure={d.income} unknown={t('dashboard.income_unknown')} />
      </Tile>
      <Tile label={t('dashboard.key.margin')} panel="margin" onOpen={onOpen} className="md:order-4">
        {d.margin ? (
          <>
            <span className="text-xl font-semibold tabular-nums">{formatMoney(d.margin)}</span>
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
        {d.committedPct != null ? <span className="text-xs text-muted-foreground">{t('dashboard.committed_share', { value: percent(d.committedPct) })}</span> : null}
      </Tile>
    </div>
  );
}
