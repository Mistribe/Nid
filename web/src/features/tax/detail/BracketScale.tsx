import { useTranslation } from 'react-i18next';
import type { TaxBracket } from '@/api/tax';
import { toPlot } from '@/lib/plot';
import { InfoTip } from '@/components/InfoTip';
import { percentText, tipText } from '../format';
import { wholeEur } from '../ResultTable';
import '@/locales/tax';

/** The scale's bands side by side (widths in plot units, the last one cut at the server's `displayEnd`), the household's band emphasised, a marker at its quotient. */
export function BracketScale({ bracket, marginalRate }: { bracket: TaxBracket; marginalRate: string }) {
  const { t } = useTranslation();
  const { bands, quotient, nextFrom, toNext, displayEnd } = bracket;
  const end = toPlot(displayEnd);
  const left = end > 0 ? Math.min(100, Math.round((toPlot(quotient) / end) * 10000) / 100) : 0;
  const align = left < 15 ? 'translate-x-0' : left > 85 ? '-translate-x-full' : '-translate-x-1/2';
  const next = nextFrom == null ? undefined : bands.find((b) => b.from === nextFrom || toPlot(b.from) === toPlot(nextFrom));
  return (
    <div className="space-y-2">
      <div className="relative pt-6">
        <div aria-hidden className="flex h-8 gap-0.5">
          {bands.map((b) => {
            // Both rates are the server's percent strings (same formatter), so a string match is exact.
            const current = b.rate === marginalRate;
            return (
              <div
                key={b.from}
                data-current={current ? 'true' : undefined}
                style={{ flexGrow: toPlot(b.to ?? displayEnd) - toPlot(b.from), flexBasis: 0 }}
                className={`flex min-w-0 items-center justify-center rounded text-xs ${current ? 'bg-chart-1 font-medium text-background' : 'bg-chart-1/25 text-foreground'}`}
              >
                {percentText(t, b.rate)}
              </div>
            );
          })}
        </div>
        <div className="absolute top-0" style={{ left: `${left}%` }}>
          <span className={`block whitespace-nowrap text-xs font-medium ${align}`}>{t('tax.bracket.you', { amount: wholeEur(quotient) })}</span>
        </div>
      </div>
      <p className="text-sm">
        {t('tax.bracket.sentence', { rate: percentText(t, marginalRate) })}{' '}
        {next && toNext != null ? t('tax.bracket.next', { amount: wholeEur(toNext), rate: percentText(t, next.rate) }) : t('tax.bracket.top')}
        <InfoTip label={t('tax.bracket.title')}>{tipText(t, 'marginal_rate')}</InfoTip>
      </p>
    </div>
  );
}
