import { useTranslation } from 'react-i18next';
import { monthLabel } from '@/lib/format';
import type { Projection } from '@/api/dashboard';
import '@/locales/scenarios';

type Warning = Projection['warnings'][number];

const month = (yyyyMm: string) => {
  const [year, m] = yyyyMm.split('-').map(Number);
  return monthLabel(year ?? 0, m ?? 1, 'long');
};

/** Spec §3.4: a scenario's warnings, the base's own in the dashboard card's words, the scenario's in their own (a count as `detail`). */
export function ProjectionWarnings({ warnings }: { warnings: Warning[] }) {
  const { t } = useTranslation();
  if (warnings.length === 0) return null;
  // A count of events, not an amount.
  const count = (w: Warning) => Number.parseInt(w.detail ?? '0', 10) || 0;
  const text = (w: Warning) => {
    switch (w.code) {
      case 'income_end_extended': return t('dashboard.projection.warning_income_end_extended', { name: w.detail });
      case 'savings_exhausted': return t('dashboard.projection.warning_savings_exhausted', { month: w.detail ? month(w.detail) : '' });
      case 'fx_missing': return t('dashboard.projection.warning_fx_missing', { code: w.detail });
      case 'hidden_targets':
      case 'event_in_past':
      case 'event_undated': return t(`scenarios.projection.warning.${w.code}`, { count: count(w) });
      case 'purchase_in_past':
      case 'tax_estimate_unavailable': return t(`scenarios.projection.warning.${w.code}`);
      default: return t('scenarios.projection.warning.unknown', { code: w.code });
    }
  };
  return (
    <ul className="space-y-0.5 text-sm text-status-warning-text">
      {warnings.map((w) => <li key={`${w.code}-${w.detail ?? ''}`}>{text(w)}</li>)}
    </ul>
  );
}
