import type { TFunction } from 'i18next';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { ModalDialog } from '@/components/ModalDialog';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ItemLink } from '../dashboard/ItemLink';
import { TAX_SALE, type SaleItemView } from './api';
import { SaleItem } from './SaleItem';
import { eur } from './ResultTable';
import { saleFindingText, saleFlagsText, saleNoteText, saleStatusText } from './saleFormat';
import '@/locales/tax';

/** « Si tout était vendu »: the household's (or one person's) sale tax, item by item, the server's figures only. */
export function SaleTotalSection({ scope }: { scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [open, setOpen] = useState<SaleItemView | null>(null);
  const answer = $api.useQuery('get', TAX_SALE, { params: { query: { scope } } });
  const view = answer.data;
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!view) return <div role="status" aria-busy="true" aria-label={t('tax.loading')} className="h-40 shimmer rounded-md border bg-muted" />;
  if (!view.items.length) return <EmptyState module="/tax" title={t('tax.sale.all.empty_title')} body={t('tax.sale.all.empty_body')} level={3} />;
  const { totals } = view;
  const money = (amount?: string | null) => (amount ? eur(amount) : '—');
  return (
    <div className="space-y-4">
      {totals ? (
        <StatLine items={[
          { label: t('tax.sale.all.value'), value: eur(totals.value) },
          { label: t('tax.sale.income_tax'), value: eur(totals.incomeTax) },
          { label: t('tax.sale.social_tax'), value: eur(totals.socialTax) },
          { label: t('tax.sale.surtax'), value: eur(totals.surtax) },
          { label: t('tax.sale.net'), value: eur(totals.net) },
        ]} />
      ) : null}
      <p className="text-sm text-muted-foreground">{[t('tax.sale.all.intro'), view.carriedForward ? t('tax.sale.carried') : null].filter(Boolean).join(' ')}</p>
      {view.notes.length ? <ul className="list-disc space-y-1 pl-5 text-sm text-muted-foreground">{view.notes.map((n) => <li key={n}>{saleNoteText(t, n)}</li>)}</ul> : null}
      <SortableTable<SaleItemView> caption={t('tax.sale.all.list')} rows={view.items} rowKey={(r) => r.item}
        columns={[
          { id: 'name', header: t('tax.sale.all.name'), wrap: true, sort: (r) => r.name, cell: (r) => <ItemLink module={r.module} id={r.item}>{r.name}</ItemLink> },
          { id: 'status', header: t('tax.sale.all.status'), wrap: true, cell: (r) => statusText(t, r) },
          { id: 'value', header: t('tax.sale.all.value'), align: 'end', sort: (r) => amountKey(r.value), cell: (r) => money(r.value) },
          { id: 'gain', header: t('tax.sale.gain'), align: 'end', sort: (r) => amountKey(r.gain), cell: (r) => money(r.gain) },
          { id: 'income', header: t('tax.sale.income_tax'), align: 'end', sort: (r) => amountKey(r.incomeTax), cell: (r) => money(r.incomeTax) },
          { id: 'social', header: t('tax.sale.social_tax'), align: 'end', wrap: true, sort: (r) => amountKey(r.socialTax), cell: (r) => money(r.socialTax) },
          { id: 'surtax', header: t('tax.sale.surtax'), align: 'end', sort: (r) => amountKey(r.surtax), cell: (r) => money(r.surtax) },
          { id: 'net', header: t('tax.sale.net'), align: 'end', sort: (r) => amountKey(r.net), cell: (r) => money(r.net) },
          { id: 'detail', header: t('tax.sale.all.detail_col'), cell: (r) => <DetailButton item={r} onOpen={setOpen} /> },
        ]}
        mobile={(r) => (
          <div className="space-y-1">
            <div className="flex items-baseline justify-between gap-2">
              <ItemLink module={r.module} id={r.item}>{r.name}</ItemLink>
              <span className="tabular-nums">{money(r.net)}</span>
            </div>
            <p className="text-sm text-muted-foreground">{statusText(t, r)}</p>
            <DetailButton item={r} onOpen={setOpen} />
          </div>
        )} />
      {open ? (
        <ModalDialog title={open.name} onClose={() => setOpen(null)}>
          {() => <SaleItem item={open} carriedForward={view.carriedForward} />}
        </ModalDialog>
      ) : null}
    </div>
  );
}

/** The status (or the finding that replaces it) and the caveats on its figures, said in words. */
function statusText(t: TFunction, r: SaleItemView): string {
  return [r.finding ? saleFindingText(t, r.finding) : saleStatusText(t, r.status), ...saleFlagsText(t, r)].join(' · ');
}

function DetailButton({ item, onOpen }: { item: SaleItemView; onOpen: (i: SaleItemView) => void }) {
  const { t } = useTranslation();
  // « Détail » on screen, the item's name for a screen reader: the name already heads the row.
  return <Button type="button" variant="link" className="h-auto p-0" aria-label={t('tax.sale.all.detail', { name: item.name })} onClick={() => onOpen(item)}>{t('tax.sale.all.detail_col')}</Button>;
}
