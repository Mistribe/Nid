import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { formatCurrency, type MoneyValue } from '@/components/Money';
import { SectionCard } from '@/components/page/SectionCard';
import { MONTH_CHANGES } from '@/api/dashboard';

const FIGURES = ['netWorth', 'commitments', 'income'] as const;

const signed = (m: MoneyValue) => formatCurrency(m.amount, m.currency, { signDisplay: 'exceptZero' });

/**
 * Issue #415: « Depuis le mois dernier » — each figure's change and its largest causes, as the server computed them. Nothing while
 * loading, on error, or in the first month (no month to compare).
 */
export function MonthChanges({ scope }: { scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { data } = $api.useQuery('get', MONTH_CHANGES, { params: { query: { scope } } });
  if (!data || data.isEmpty) return null;
  return (
    <SectionCard title={t('dashboard.changes.title')} note={t('dashboard.changes.since', { date: formatLocalDate(data.since) })}>
      <ul className="grid gap-4 sm:grid-cols-3">
        {FIGURES.map((key) => {
          const f = data[key];
          if (!f) return null;
          return (
            <li key={key} className="space-y-1">
              <p className="text-sm text-muted-foreground">{t(`dashboard.changes.${key}`)}</p>
              <p className="text-lg font-semibold">{signed(f.change)}</p>
              {f.causes.length > 0 ? (
                <ul className="text-sm text-muted-foreground">
                  {f.causes.map((c) => (
                    <li key={c.key}>
                      {t('dashboard.changes.cause_line', {
                        amount: signed(c.change),
                        cause: t(`dashboard.changes.cause.${c.key}`, { defaultValue: t('dashboard.changes.cause.other') }),
                      })}
                    </li>
                  ))}
                </ul>
              ) : null}
            </li>
          );
        })}
      </ul>
    </SectionCard>
  );
}
