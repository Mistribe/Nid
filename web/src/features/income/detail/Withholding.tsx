import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { percent } from '@/lib/format';
import type { IncomeSourceOverview } from '@/api/income';
import '@/locales/income';

/**
 * #408: the owner's withholding rate against the real rate the household's tax estimate gives, and what that withholds each month
 * beyond it, with the tax page's way to adjust the rate. The server sends it only for an open salary once the tax estimate is used.
 */
export function Withholding({ items }: { items: IncomeSourceOverview['withholding'] }) {
  const { t } = useTranslation();
  if (items.length === 0) return null;
  return (
    <div className="mt-3 space-y-1 text-sm">
      {items.map((w) => {
        const gap = w.monthlyGap.amount.replace('-', '');
        const lent = !w.monthlyGap.amount.startsWith('-');
        return (
          <p key={w.person}>
            {t('income_detail.withholding.rates', { rate: percent(w.rate), real: percent(w.realRate) })}
            {items.length > 1 ? <span className="text-muted-foreground"> · {w.name}</span> : null}
            {/^0(\.0+)?$/.test(gap) ? null : (
              <span className="block text-muted-foreground">
                {t(lent ? 'income_detail.withholding.lent' : 'income_detail.withholding.short', { amount: formatMoney({ ...w.monthlyGap, amount: gap }), name: w.name })}
              </span>
            )}
          </p>
        );
      })}
      <p className="text-muted-foreground">
        {t('income_detail.withholding.indicative')}{' '}
        <Link to="/tax" search={{ tab: 'withholding' }} className="underline">{t('income_detail.withholding.adjust')}</Link>
      </p>
    </div>
  );
}
