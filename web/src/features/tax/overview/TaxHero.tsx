import { useTranslation } from 'react-i18next';
import type { TaxEstimate } from '@/api/tax';
import { Hero, HeroChange, HeroCurve } from '@/components/page/Hero';
import { StatLine } from '@/components/page/StatLine';
import { Button } from '@/components/ui/button';
import { toPlot } from '@/lib/plot';
import { balanceWords, unsigned } from '../balance';
import { partsText, percentText, signedPercentText, tipText } from '../format';
import { eur } from '../ResultTable';
import type { TaxTab } from '../TaxPage';
import '@/locales/tax';

/** The previous year's scale in use: said in the note, where the scale is named every year. */
const CARRIED = 'rules_carried_forward';

/** The overview's lead: the year's total tax and its change, the curve of the years, and the six figures that explain it. */
export function TaxHero({ estimate, onTab }: { estimate: TaxEstimate; onTab: (tab: TaxTab) => void }) {
  const { t } = useTranslation();
  const r = estimate.result;
  const { previous, history } = estimate;
  const balance = r.withholding.balance;
  const note = [
    r.averageRate ? t('tax.figure.average_rate', { rate: percentText(t, r.averageRate) }) : null,
    t(r.warnings.includes(CARRIED) ? 'tax.figure.carried' : 'tax.figure.scale', { year: r.rulesYear }),
  ].filter(Boolean).join(' · ');
  return (
    <Hero label={t('tax.hero.label', { year: estimate.year })} figure={eur(r.totalTax)} note={note}
      change={previous ? (
        <span className="flex flex-wrap items-center gap-x-2">
          <span>{t('tax.hero.change_of')}</span>
          <HeroChange amount={{ amount: previous.change, currency: 'EUR' }} pct={previous.changePct ? signedPercentText(t, previous.changePct) : null} rising="bad">
            {t(previous.source === 'assessment' ? 'tax.hero.since_assessment' : 'tax.hero.since_estimate', { year: previous.year })}
          </HeroChange>
        </span>
      ) : undefined}
      curve={<HeroCurve values={(history ?? []).map((y) => toPlot(y.tax))} />}
      aside={(
        <StatLine className="md:grid-cols-3" items={[
          { label: t('tax.overview.left_monthly'), value: eur(r.netAfterTaxMonthly), tip: tipText(t, 'left_monthly') },
          { label: balanceWords(t, balance, estimate.year), value: eur(unsigned(balance)), tip: tipText(t, 'balance'),
            hint: <Button variant="link" className="h-auto p-0" onClick={() => onTab('withholding')}>{t('tax.overview.adjust')}</Button> },
          { label: t('tax.overview.net_taxable'), value: eur(r.netTaxableIncome), tip: tipText(t, 'net_taxable') },
          { label: t('tax.overview.marginal_rate'), value: percentText(t, r.marginalRate), tip: tipText(t, 'marginal_rate') },
          { label: t('tax.overview.withholding_rate'), value: percentText(t, r.withholding.rate), tip: tipText(t, 'withholding_rate') },
          { label: t('tax.overview.parts'), value: partsText(r.parts), tip: tipText(t, 'parts') },
        ]} />
      )} />
  );
}
