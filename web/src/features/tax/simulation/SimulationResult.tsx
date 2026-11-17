import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import type { TaxResult } from '@/api/tax';
import { Direction } from '@/components/Direction';
import { Folded } from '@/components/form/Folded';
import { SectionCard } from '@/components/page/SectionCard';
import { StatLine } from '@/components/page/StatLine';
import { balanceWords, unsigned } from '../balance';
import { ZERO } from '../format';
import { eur, ResultTable } from '../ResultTable';
import '@/locales/tax';

/** The simulation against the estimate: three figures with the server's differences, the two-column table folded; scrolled into view when it appears. */
export function SimulationResult({ before, after, year }: { before: TaxResult; after: TaxResult; year: number }) {
  const { t } = useTranslation();
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    root.current?.scrollIntoView({ block: 'start', behavior: reduce ? 'auto' : 'smooth' });
  }, [after]);
  const diff = after.differences;
  const hint = (amount: string | undefined, rising: 'good' | 'bad', was: string) => (
    <>
      {amount === undefined ? null : <Direction amount={{ amount, currency: 'EUR' }} rising={rising} />}
      {amount === undefined ? was : <span className="block">{was}</span>}
    </>
  );
  const wasBalance = before.withholding.balance;
  const wasText = ZERO.test(wasBalance) ? t('tax.simulation.before_none')
    : t(wasBalance.startsWith('-') ? 'tax.simulation.before_refund' : 'tax.simulation.before_to_pay', { amount: eur(unsigned(wasBalance)) });
  return (
    <div ref={root} role="status" className="scroll-mt-4">
      <SectionCard title={t('tax.simulation.result_title')} className="space-y-3">
        <StatLine items={[
          { label: t('tax.simulation.total_tax'), value: eur(after.totalTax),
            hint: hint(diff?.totalTax, 'bad', t('tax.simulation.before', { value: eur(before.totalTax) })) },
          { label: t('tax.simulation.left_monthly'), value: eur(after.netAfterTaxMonthly),
            hint: hint(diff?.netAfterTaxMonthly, 'good', t('tax.simulation.before', { value: eur(before.netAfterTaxMonthly) })) },
          { label: balanceWords(t, after.withholding.balance, year), value: eur(unsigned(after.withholding.balance)),
            hint: hint(diff?.balance, 'bad', wasText) },
        ]} />
        <Folded label={t('tax.simulation.detail')} hasError={false}>
          <ResultTable label={t('tax.simulation.result')}
            columns={[{ title: t('tax.estimate'), result: before }, { title: t('tax.simulation.column'), result: after }]} />
        </Folded>
      </SectionCard>
    </div>
  );
}
