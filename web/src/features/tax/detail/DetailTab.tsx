import { useTranslation } from 'react-i18next';
import type { TaxEstimate, TaxResult } from '@/api/tax';
import { InfoTip } from '@/components/InfoTip';
import { SectionCard } from '@/components/page/SectionCard';
import { Button } from '@/components/ui/button';
import { regimeText, tipText, ZERO } from '../format';
import { PerRows, PerSummary } from '../PerSection';
import { DeclarantTable, eur, ResultTable } from '../ResultTable';
import type { TaxTab } from '../TaxPage';
import '@/locales/tax';

/** The calculation step by step, with the regime and the capital option as questions. */
export function DetailTab({ estimate, onTab }: { estimate: TaxEstimate; onTab: (tab: TaxTab) => void }) {
  const { t } = useTranslation();
  const onProfile = () => onTab('profile');
  return (
    <SectionCard title={t('tax.detail_title')} className="space-y-4">
      <p className="text-sm text-muted-foreground">{t('tax.disclaimer', { year: estimate.result.rulesYear })}</p>
      <DeclarantTable result={estimate.result} detail={estimate.salaryDetail} adults={estimate.adults}>
        <PerRows result={estimate.result} names={estimate.result.declarants.map((_, i) => (estimate.inputs.declarants[i]?.perEnvelopes ?? []).join(', '))} />
      </DeclarantTable>
      <PerSummary result={estimate.result} year={estimate.year} onProfile={onProfile} />
      <ResultTable label={t('tax.detail_table')} declarants={false} columns={[{ title: t('tax.estimate'), result: estimate.result }]} />
      <RegimeChoice result={estimate.result} onProfile={onProfile} />
      <CapitalChoice result={estimate.result} onProfile={onProfile} />
      <p className="text-sm text-muted-foreground">{t('tax.out_of_scope')}</p>
    </SectionCard>
  );
}

/** #234: the capital income's option as a question, with what the other one would cost (the server's gap) and next year's 6DE. */
function CapitalChoice({ result: r, onProfile }: { result: TaxResult; onProfile: () => void }) {
  const { t } = useTranslation();
  const gap = r.capitalOptionGap;
  if (!gap) return null;
  const comparison = ZERO.test(gap) ? t('tax.capital_choice.same')
    : gap.startsWith('-') ? t('tax.capital_choice.less', { amount: eur(gap.slice(1)) }) : t('tax.capital_choice.more', { amount: eur(gap) });
  return (
    <div className="max-w-3xl space-y-1 text-sm">
      <p className="flex items-center gap-1.5 font-medium">{t('tax.capital_choice.question')}<InfoTip label={t('tax.capital_choice.question')}>{tipText(t, 'capital_option')}</InfoTip></p>
      <p>
        {t(r.capitalScale ? 'tax.capital_choice.current_scale' : 'tax.capital_choice.current_flat')} {comparison}{' '}
        {r.capitalScale && !ZERO.test(r.deductibleCsgNextYear) ? <>{t('tax.capital_choice.csg', { amount: eur(r.deductibleCsgNextYear) })}{' '}</> : null}
        <Button variant="link" className="h-auto p-0 text-sm" onClick={onProfile}>{t('tax.capital_choice.change')}</Button>
      </p>
    </div>
  );
}

/** Issue #160: the rental regime as a question, with what the other one would cost: the server's gap (negative, it costs less). */
function RegimeChoice({ result: r, onProfile }: { result: TaxResult; onProfile: () => void }) {
  const { t } = useTranslation();
  const gap = r.rentalRegimeGap;
  if (!gap) return null;
  const other = regimeText(t, r.rentalRegime === 'micro' ? 'real' : 'micro');
  const comparison = ZERO.test(gap) ? t('tax.regime_choice.same', { other })
    : gap.startsWith('-') ? t('tax.regime_choice.less', { other, amount: eur(gap.slice(1)) }) : t('tax.regime_choice.more', { other, amount: eur(gap) });
  return (
    <div className="max-w-3xl space-y-1 text-sm">
      <p className="flex items-center gap-1.5 font-medium">{t('tax.regime_choice.question')}<InfoTip label={t('tax.regime_choice.question')}>{tipText(t, 'regime')}</InfoTip></p>
      <p>
        {t('tax.regime_choice.current', { regime: regimeText(t, r.rentalRegime) })} {comparison}{' '}
        <Button variant="link" className="h-auto p-0 text-sm" onClick={onProfile}>{t('tax.regime_choice.change')}</Button>
      </p>
    </div>
  );
}
