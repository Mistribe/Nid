import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { Section } from '@/components/page/Section';
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { TAX_CHANGES, type TaxEstimate } from '@/api/tax';
import { partsText, ZERO } from './format';
import { eur } from './ResultTable';
import '@/locales/tax';

const LINES = ['salaries', 'rental_income', 'rental_deficit', 'per_deduction', 'shares', 'decote', 'pinel', 'donations', 'childcare', 'home_employment', 'flat_tax'] as const;

/** The effect of a line on the tax, signed: a rise reads « +1 200,00 € ». */
const signed = (amount: string) => (amount.startsWith('-') || ZERO.test(amount) ? eur(amount) : `+${eur(amount)}`);

/**
 * Issue #410: « Ce qui a changé depuis {année} », each line of the calculation that moved, last year's figure (the notice's when
 * it prints it) against this year's estimate and its effect on the tax, sorted by the server. Every figure is the server's.
 */
export function ChangesSection({ year, scope, adults }: { year: string; scope: 'me' | 'household'; adults: TaxEstimate['adults'] }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const changes = $api.useQuery('get', TAX_CHANGES, { params: { query: { year: Number(year), scope } } }).data;
  if (!changes?.taxBefore && !changes?.lines.length) return null;
  const since = String(Number(year) - 1);
  const label = (code: string, person?: string | null) => {
    if (code === 'salaries') return t('tax.changes.line.salaries', { name: adults.find((a) => a.id === person)?.displayName ?? '' });
    const known = LINES.find((l) => l === code);
    return known ? t(`tax.changes.line.${known}`) : code;
  };
  const value = (code: string, amount: string) => (code === 'shares' ? partsText(amount) : eur(amount));
  return (
    <Section title={t('tax.changes.title', { year: since })}>
      <p className="text-sm text-muted-foreground">{t('tax.changes.intro', { year })}</p>
      {changes.lines.length ? (
        <Table aria-label={t('tax.changes.title', { year: since })}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('tax.changes.line_head')}</TableHead>
              <TableHead className="text-right">{since}</TableHead>
              <TableHead className="text-right">{year}</TableHead>
              <TableHead className="text-right">{t('tax.changes.effect')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {changes.lines.map((l) => (
              <TableRow key={`${l.code}-${l.person ?? ''}`}>
                <TableCell>{label(l.code, l.person)}</TableCell>
                <TableCell className="text-right tabular-nums">
                  {value(l.code, l.before)}
                  {l.fromNotice ? <div className="text-xs text-muted-foreground">{t('tax.changes.from_notice')}</div> : null}
                </TableCell>
                <TableCell className="text-right tabular-nums">{value(l.code, l.now)}</TableCell>
                <TableCell className="text-right tabular-nums font-medium">{signed(l.effect)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
          {changes.taxBefore && changes.taxNow ? (
            <TableFooter>
              {changes.remainder && !ZERO.test(changes.remainder) ? (
                <TableRow>
                  <TableCell colSpan={3}>{t('tax.changes.remainder')}</TableCell>
                  <TableCell className="text-right tabular-nums">{signed(changes.remainder)}</TableCell>
                </TableRow>
              ) : null}
              <TableRow>
                <TableCell>{t('tax.changes.tax')}</TableCell>
                <TableCell className="text-right tabular-nums">
                  {eur(changes.taxBefore)}
                  {changes.taxBeforeFromNotice ? <div className="text-xs font-normal text-muted-foreground">{t('tax.changes.from_notice')}</div> : null}
                </TableCell>
                <TableCell className="text-right tabular-nums">{eur(changes.taxNow)}</TableCell>
                <TableCell />
              </TableRow>
            </TableFooter>
          ) : null}
        </Table>
      ) : <p className="text-sm text-muted-foreground">{t('tax.changes.none')}</p>}
    </Section>
  );
}
