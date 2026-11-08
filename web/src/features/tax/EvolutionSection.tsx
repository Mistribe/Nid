import { useQueries } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '@/lib/plot';
import { FIRST_ESTIMATE_YEAR, TAX, type TaxEstimate } from './api';
import { ChangesSection } from './ChangesSection';
import type { TaxAssessment } from './assessments/api';
import { percentText, ZERO } from './format';
import { eur } from './ResultTable';
import '@/locales/tax';

type Row = { year: string; source: 'assessment' | 'estimate'; netTaxable?: string; tax: string; flatTax?: string; rate?: string | null };

/**
 * The income tax year by year: a year's notice when there is one, else the server's estimate (from 2025). The notice's
 * « impôt net » holds the flat tax on capital income; the estimate's income tax does not, so its flat tax is shown beside it
 * rather than added here. The bars are sized with `toPlot`; every figure read is the server's.
 */
export function EvolutionSection({ years, assessments, scope, current, adults }: {
  years: string[]; assessments: TaxAssessment[]; scope: 'me' | 'household'; current: string; adults: TaxEstimate['adults'];
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const notice = (year: string) => assessments.find((a) => String(a.year) === year && a.netTax);
  const estimated = years.filter((y) => !notice(y) && Number(y) >= FIRST_ESTIMATE_YEAR);
  // The same queries as the page's own estimate: the selected year comes from the cache.
  const answers = useQueries({ queries: estimated.map((y) => $api.queryOptions('get', TAX, { params: { query: { year: Number(y), scope } } })) });
  const rows: Row[] = years.flatMap((year): Row[] => {
    const a = notice(year);
    if (a?.netTax) return [{ year, source: 'assessment', netTaxable: a.taxableIncome?.amount, tax: a.netTax.amount, rate: a.effectiveAverageRate }];
    const r = answers[estimated.indexOf(year)]?.data?.result;
    if (!r) return [];
    return [{ year, source: 'estimate', netTaxable: r.netTaxableIncome, tax: r.incomeTax, flatTax: ZERO.test(r.capitalFlatTax) ? undefined : r.capitalFlatTax, rate: r.averageRate }];
  });
  const loading = answers.some((q) => q.isPending);
  const sizes = rows.map((r) => Math.max(0, toPlot(r.tax)));
  const largest = Math.max(1, ...sizes);

  if (!rows.length) return loading ? <div aria-busy="true" aria-label={t('tax.loading')} className="h-40 shimmer rounded-md border bg-muted" /> : <p className="text-sm text-muted-foreground">{t('tax.evolution.empty')}</p>;
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('tax.evolution.intro')}</p>
      <Table aria-label={t('tax.evolution.title')} aria-busy={loading || undefined}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('tax.evolution.year')}</TableHead>
            <TableHead className="text-right">{t('tax.figure.net_taxable')}</TableHead>
            <TableHead className="text-right">{t('tax.figure.income_tax')}</TableHead>
            <TableHead className="w-2/5"><span className="sr-only">{t('tax.evolution.bar')}</span></TableHead>
            <TableHead className="text-right">{t('tax.evolution.rate')}</TableHead>
            <TableHead>{t('tax.evolution.source')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((r, i) => (
            <TableRow key={r.year} aria-current={r.year === current ? 'true' : undefined} className={r.year === current ? 'font-medium' : undefined}>
              <TableCell>{r.year}</TableCell>
              <TableCell className="text-right tabular-nums">{r.netTaxable ? eur(r.netTaxable) : '—'}</TableCell>
              <TableCell className="text-right tabular-nums">
                {eur(r.tax)}
                {r.flatTax ? <div className="text-xs font-normal text-muted-foreground">{t('tax.evolution.flat_tax', { amount: eur(r.flatTax) })}</div> : null}
              </TableCell>
              <TableCell aria-hidden="true">
                <div className={`h-2.5 rounded-full ${r.source === 'assessment' ? 'bg-chart-1' : 'bg-chart-1/50'}`} style={{ width: `${(sizes[i]! / largest) * 100}%` }} />
              </TableCell>
              <TableCell className="text-right tabular-nums">{r.rate ? percentText(t, r.rate) : '—'}</TableCell>
              <TableCell className="text-muted-foreground">{t(`tax.evolution.from.${r.source}`)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <ChangesSection year={current} scope={scope} adults={adults} />
    </>
  );
}
