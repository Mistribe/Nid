import { useTranslation } from 'react-i18next';
import type { TaxEstimate } from '@/api/tax';
import { EmptyState } from '@/components/EmptyState';
import { OwnerChips } from '@/components/OwnerChips';
import { SectionCard } from '@/components/page/SectionCard';
import { Button } from '@/components/ui/button';
import { downloadBlob } from '@/lib/download';
import { todayIso } from '@/lib/today';
import { declaration2042Csv } from '../declaration2042Csv';
import { BoxList } from './BoxList';
import { boxText, boxTip, noticeAmount, noticeText } from '../format';
import { eur } from '../ResultTable';
import '@/locales/tax';

/**
 * Issue #233: the 2042 boxes the estimate fills, with their declarant, then what Nid leaves for the household to enter. Every figure
 * is the server's string; the CSV copies them as received.
 */
/** A box goes to the section of its first character on the form; the rest to « other ». */
const BOX_SECTIONS = ['1', '2', '4', '6', '7', 'other'] as const;

export function Declaration2042({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const declaration = estimate.declaration ?? { boxes: [], toEnter: [] };
  const names = new Map(estimate.adults.map((a) => [a.id, a.displayName]));
  const personName = (person?: string | null) => (person && names.get(person)) || t('tax.d2042.household');
  const sectionOf = (code: string) => BOX_SECTIONS.find((k) => k === code[0]) ?? 'other';
  const provisional = estimate.year >= Number(todayIso().slice(0, 4));
  return (
    <>
      <SectionCard title={t('tax.d2042.filled')} note={provisional ? t('tax.d2042.provisional') : undefined} className="space-y-4"
        actions={declaration.boxes.length ? (
          <>
            <Button type="button" variant="outline" className="print:hidden"
              onClick={() => downloadBlob(new Blob([declaration2042Csv(declaration, personName, t)], { type: 'text/csv' }), `declaration-2042-${estimate.year}.csv`)}>
              {t('tax.d2042.csv')}
            </Button>
            <Button type="button" variant="outline" className="print:hidden" onClick={() => window.print()}>{t('tax.d2042.print')}</Button>
          </>
        ) : undefined}>
        {declaration.boxes.length ? BOX_SECTIONS.map((section) => {
          const rows = declaration.boxes.filter((b) => sectionOf(b.code) === section).map((b) => ({
            code: b.code, title: boxText(t, b.code), tip: boxTip(t, b.code), amount: b.amount,
            owner: b.person ? <OwnerChips owners={[{ personId: b.person, shareBp: 10000 }]} shares={false} /> : <span className="text-sm text-muted-foreground">{t('tax.d2042.household')}</span>,
          }));
          const title = t(`tax.d2042.section.${section}`);
          return rows.length ? (
            <div key={section}>
              <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">{title}</h3>
              <BoxList label={title} rows={rows} />
            </div>
          ) : null;
        }) : <EmptyState module="/tax" title={t('tax.d2042.empty_title')} body={t('tax.d2042.empty_body')} />}
      </SectionCard>
      {declaration.toEnter.length ? (
        <SectionCard title={t('tax.d2042.to_enter')}>
          <p className="text-sm text-muted-foreground">{t('tax.d2042.to_enter_help')}</p>
          <ul className="list-disc space-y-1 pl-5 text-sm">
            {declaration.toEnter.map((n, i) => <li key={`${n.code}-${n.item}-${i}`}>{noticeText(t, n.code, n.name, noticeAmount(t, n.code, n.amount, eur))}</li>)}
          </ul>
        </SectionCard>
      ) : null}
    </>
  );
}
