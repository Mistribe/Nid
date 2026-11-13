import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import type { LegalMinimum } from './api';
import '@/locales/childcare';

/**
 * The legal minimums the contract's terms fall below (server's check, figures as it sends them): a warning suggesting a revision of
 * the terms, never a refusal. Nothing when the terms meet them.
 */
export function BelowMinimums({ items, action }: { items: readonly LegalMinimum[] | null | undefined; action?: ReactNode }) {
  const { t } = useTranslation();
  if (!items?.length) return null;
  return (
    <section role="status" aria-label={t('childcare_detail.minimums.title')}
      className="space-y-2 rounded-md border border-status-warning/40 bg-status-warning/10 p-3 text-sm text-status-warning-text">
      <p className="font-medium">{t('childcare_detail.minimums.title')}</p>
      <ul className="list-disc space-y-1 pl-5">
        {items.map((m) => (
          <li key={m.code}>
            {t(m.code === 'childcare.below_minimum_upkeep' ? 'childcare_detail.minimums.upkeep'
              : m.code === 'childcare.below_minimum_gross_rate' ? 'childcare_detail.minimums.gross_rate' : 'childcare_detail.minimums.net_rate', {
              value: m.value.replace('.', ','), minimum: m.minimum.replace('.', ','), since: formatLocalDate(m.since),
            })}
          </li>
        ))}
      </ul>
      <p>{t('childcare_detail.minimums.hint')}</p>
      {action}
    </section>
  );
}
