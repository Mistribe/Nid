import type { TFunction } from 'i18next';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { formatMoney, Money } from '@/components/Money';
import { percent, monthLabel } from '@/lib/format';
import type { ScenarioResult } from '../api';
import '@/locales/scenarios';

type Warning = components['schemas']['ScenarioWarningView'];

/** A warning in words (spec §4.3); its value is the server's (a signed gap, a count, a month, a currency). */
export function warningText(t: TFunction, w: Warning, currency: string): string {
  switch (w.code) {
    case 'funding_gap': return t('scenarios.warning.funding_gap', { amount: formatMoney({ amount: w.value ?? '0', currency }) });
    case 'funding_source_hidden': return t('scenarios.warning.funding_source_hidden', { count: Number.parseInt(w.value ?? '1', 10) });
    // `YYYY-MM` as « mars 2028 ».
    case 'savings_exhausted': {
      const [year, month] = (w.value ?? '').split('-').map(Number);
      return t('scenarios.warning.savings_exhausted', { month: monthLabel(year ?? 0, month ?? 1) });
    }
    case 'fx_missing': return t('scenarios.warning.fx_missing', { code: w.value ?? '' });
    case 'debt_ratio_above_hcsf': case 'no_income': case 'tax_estimate_unavailable': case 'purchase_in_past':
      return t(`scenarios.warning.${w.code}`);
    default: return t('scenarios.warning.unknown', { code: w.code });
  }
}

/** The capacity at a glance (spec §4.1): the server's figures only, its warnings, and the standing reminder. */
export function CapacitySummary({ result }: { result: ScenarioResult }) {
  const { t } = useTranslation();
  const c = result.capacity;
  const ratio = (r: number | null | undefined) => (r == null ? t('scenarios.dash') : t('scenarios.percent', { value: percent(r) }));
  const row = (label: string, value: ReactNode) => (
    <div className="flex justify-between gap-3">
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="text-right tabular-nums">{value}</dd>
    </div>
  );
  return (
    <div className="space-y-3 text-sm">
      <dl className="space-y-1">
        {row(t('scenarios.summary.new_payment'), <Money value={c.newPayment} tabular />)}
        {row(t('scenarios.summary.debt_before'), ratio(c.debtRatioBefore))}
        {row(t('scenarios.summary.debt_after'), ratio(c.debtRatioAfter))}
        {row(t('scenarios.summary.left_before'), <Money value={c.leftBefore} tabular />)}
        {row(t('scenarios.summary.left_after'), <Money value={c.leftAfter} tabular />)}
        {row(t('scenarios.summary.loans_cost'), <Money value={c.loansCost} tabular />)}
        {row(t('scenarios.summary.with_at_horizon'), <Money value={result.withAtHorizon} tabular />)}
        {row(t('scenarios.summary.without_at_horizon'), <Money value={result.withoutAtHorizon} tabular />)}
        {row(t('scenarios.summary.gap'), <Money value={result.gap} tabular />)}
      </dl>
      {result.warnings.length > 0 ? (
        <ul className="space-y-1 text-status-warning-text">
          {result.warnings.map((w, i) => <li key={`${w.code}-${i}`}>{warningText(t, w, c.newPayment.currency)}</li>)}
        </ul>
      ) : null}
      <p className="text-xs text-muted-foreground">{t('scenarios.summary.reminder')}</p>
    </div>
  );
}
