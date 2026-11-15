import { useTranslation } from 'react-i18next';
import type { TaxEstimate } from '@/api/tax';
import { EmptyState } from '@/components/EmptyState';
import { InfoTip } from '@/components/InfoTip';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { downloadBlob } from '@/lib/download';
import { todayIso } from '@/lib/today';
import { declaration2042Csv } from './declaration2042Csv';
import { boxText, boxTip, noticeAmount, noticeText } from './format';
import { eur, Term } from './ResultTable';
import '@/locales/tax';

/**
 * Issue #233: the 2042 boxes the estimate fills, with their declarant, then what Nid leaves for the household to enter. Every figure
 * is the server's string; the CSV copies them as received.
 */
export function Declaration2042Section({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const declaration = estimate.declaration ?? { boxes: [], toEnter: [] };
  const names = new Map(estimate.adults.map((a) => [a.id, a.displayName]));
  const personName = (person?: string | null) => (person && names.get(person)) || t('tax.d2042.household');
  const provisional = estimate.year >= Number(todayIso().slice(0, 4));
  return (
    <>
      <p className="flex items-center gap-1.5 text-sm text-muted-foreground print:hidden">{t('tax.d2042.intro')}<InfoTip label={t('tax.d2042.intro')}>{t('tax.d2042.tip')}</InfoTip></p>
      {declaration.boxes.length ? (
        <div className="flex flex-wrap items-center gap-2 print:hidden">
          <Button type="button" variant="outline"
            onClick={() => downloadBlob(new Blob([declaration2042Csv(declaration, personName, t)], { type: 'text/csv' }), `declaration-2042-${estimate.year}.csv`)}>
            {t('tax.d2042.csv')}
          </Button>
          <Button type="button" variant="outline" onClick={() => window.print()}>{t('tax.d2042.print')}</Button>
        </div>
      ) : null}
      {provisional ? <p className="text-sm text-muted-foreground">{t('tax.d2042.provisional')}</p> : null}
      {declaration.boxes.length ? (
        <dl aria-label={t('tax.tab.2042')} className="grid grid-cols-[auto_1fr_auto_auto] gap-x-4 gap-y-1 text-sm">
          {declaration.boxes.map((b) => (
            <div key={b.code} className="col-span-4 grid grid-cols-subgrid items-baseline border-b py-1 last:border-b-0">
              <dt className="contents">
                <span className="font-mono text-muted-foreground">{b.code}</span>
                <span><Term title={boxText(t, b.code)} tip={boxTip(t, b.code)} /></span>
                <span className="text-muted-foreground">{personName(b.person)}</span>
              </dt>
              <dd className="text-right font-medium tabular-nums">{eur(b.amount)}</dd>
            </div>
          ))}
        </dl>
      ) : <EmptyState module="/tax" title={t('tax.d2042.empty_title')} body={t('tax.d2042.empty_body')} />}
      {declaration.toEnter.length ? (
        <Section title={t('tax.d2042.to_enter')}>
          <p className="text-sm text-muted-foreground">{t('tax.d2042.to_enter_help')}</p>
          <ul className="list-disc space-y-1 pl-5 text-sm">
            {declaration.toEnter.map((n, i) => <li key={`${n.code}-${n.item}-${i}`}>{noticeText(t, n.code, n.name, noticeAmount(t, n.code, n.amount, eur))}</li>)}
          </ul>
        </Section>
      ) : null}
    </>
  );
}
