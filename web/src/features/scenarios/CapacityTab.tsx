import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Money } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { percent } from '@/lib/format';
import { useLiquidityName } from '@/features/dashboard/panels/allocationNames';
import type { ScenarioResult } from './api';
import { warningText } from './new/CapacitySummary';
import '@/locales/scenarios';

/** Spec §4.1, §7 « Capacité »: before and after side by side, the savings left by liquidity, then the warnings. */
export function CapacityTab({ result }: { result: ScenarioResult }) {
  const { t } = useTranslation();
  const liquidityName = useLiquidityName();
  const c = result.capacity;
  const ratio = (r: number | null | undefined) => (r == null ? t('scenarios.dash') : t('scenarios.percent', { value: percent(r) }));
  const num = 'text-right tabular-nums';
  return (
    <>
      <div className="grid gap-6 lg:grid-cols-2">
        <Section title={t('scenarios.detail.before_after')}>
          <Table aria-label={t('scenarios.detail.before_after')}>
            <TableHeader>
              <TableRow>
                <TableHead><span className="sr-only">{t('scenarios.detail.col_figure')}</span></TableHead>
                <TableHead className="text-right">{t('scenarios.detail.col_before')}</TableHead>
                <TableHead className="text-right">{t('scenarios.detail.col_after')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow>
                <TableHead scope="row" className="font-normal">{t('scenarios.detail.debt_ratio')}</TableHead>
                <TableCell className={num}>{ratio(c.debtRatioBefore)}</TableCell>
                <TableCell className={num}>{ratio(c.debtRatioAfter)}</TableCell>
              </TableRow>
              <TableRow>
                <TableHead scope="row" className="font-normal">{t('scenarios.detail.left')}</TableHead>
                <TableCell className={num}><Money value={c.leftBefore} tabular /></TableCell>
                <TableCell className={num}><Money value={c.leftAfter} tabular /></TableCell>
              </TableRow>
            </TableBody>
          </Table>
          <dl className="space-y-1 text-sm">
            {([
              [t('scenarios.detail.payment'), c.newPayment],
              [t('scenarios.detail.tax_increase'), c.monthlyTaxIncrease],
              [t('scenarios.summary.loans_cost'), c.loansCost],
            ] as const).map(([label, value]) => (
              <div key={label} className="flex justify-between gap-3">
                <dt className="text-muted-foreground">{label}</dt>
                <dd><Money value={value} tabular /></dd>
              </div>
            ))}
          </dl>
        </Section>
        <Section title={t('scenarios.detail.savings')}>
          <Table aria-label={t('scenarios.detail.savings')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('scenarios.detail.col_liquidity')}</TableHead>
                <TableHead className="text-right">{t('scenarios.detail.col_amount')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {Object.entries(c.savingsAfterByLiquidity).map(([liquidity, amount]) => (
                <TableRow key={liquidity}>
                  <TableHead scope="row" className="font-normal">{liquidityName(liquidity)}</TableHead>
                  <TableCell className={num}><Money value={amount} tabular /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
      </div>
      <Section title={t('scenarios.detail.warnings')}>
        {result.warnings.length === 0 ? <p className="text-sm text-muted-foreground">{t('scenarios.detail.no_warnings')}</p> : (
          <ul className="space-y-1">
            {result.warnings.map((w, i) => (
              <li key={`${w.code}-${i}`} className="flex items-start gap-2">
                <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-status-warning" />
                {warningText(t, w, c.newPayment.currency)}
              </li>
            ))}
          </ul>
        )}
      </Section>
    </>
  );
}
