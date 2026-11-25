import { useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { ASSET_VALUATION, invalidateAssets, type AssetValuation, type AssetView } from '@/api/assets';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { RowActions } from '@/components/page/RowActions';
import { Section } from '@/components/page/Section';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { presentConflict } from '@/components/form/fieldErrors';
import '@/locales/assets';

/** The values of an asset, newest first (the server lists them oldest first). Adding is the page header's action. */
export function AssetValuationsSection({ asset }: { asset: AssetView }) {
  const { t } = useTranslation();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const writable = asset.canWrite;
  const newestFirst = [...asset.valuations].reverse();
  return (
    <Section title={t('assets_valuations.title')} headingRef={headingRef}>
      <Table aria-label={t('assets_valuations.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('assets_valuations.col_date')}</TableHead>
            <TableHead className="text-right">{t('assets_valuations.col_value')}</TableHead>
            <TableHead>{t('assets_valuations.col_note')}</TableHead>
            {writable ? <TableHead className="w-0"><span className="sr-only">{t('assets_valuations.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {newestFirst.length === 0 ? (
            <TableRow><TableCell colSpan={writable ? 4 : 3} className="text-muted-foreground">{t('assets_valuations.empty')}</TableCell></TableRow>
          ) : newestFirst.map((v) => (
            <TableRow key={v.id}>
              <TableCell><LocalDate value={v.asOf} /></TableCell>
              <TableCell className="text-right tabular-nums"><Money value={v.amount} /></TableCell>
              <TableCell className="whitespace-normal">{v.note}</TableCell>
              {writable ? (
                <TableCell>
                  <DeleteValuation asset={asset} valuation={v} onDeleted={(m) => { toast.success(m); headingRef.current?.focus(); }} onReloaded={() => headingRef.current?.focus()} />
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}

/** « Supprimer » asks first; the last value of an asset without a purchase price is refused by the server, which says why. */
function DeleteValuation({ asset, valuation, onDeleted, onReloaded }: {
  asset: AssetView; valuation: AssetValuation; onDeleted: (message: string) => void; onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', ASSET_VALUATION, {
    onSuccess: () => { void invalidateAssets(queryClient); onDeleted(t('assets_valuations.deleted')); },
  });
  return (
    <RowActions menuId={`delete-asset-valuation-${valuation.id}`} menuLabel={t('assets_valuations.row_menu', { date: formatLocalDate(valuation.asOf) })}
      items={[]} confirmation={t('assets_valuations.delete_confirm')} remove={remove} onReloaded={onReloaded}
      present={presentConflict} invalidate={invalidateAssets}
      onConfirm={() => remove.mutate({ params: { path: { id: asset.id, valuationId: valuation.id } } })} />
  );
}
