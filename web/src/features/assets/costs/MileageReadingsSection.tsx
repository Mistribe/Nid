import { useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { ASSET_MILEAGE_READING, invalidateAssets, type AssetView } from '@/api/assets';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { presentConflict } from '@/components/form/fieldErrors';
import { RowActions } from '@/components/page/RowActions';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import '@/locales/assets';

/** #650: a vehicle's mileage readings, newest first; a wrong one, which would skew the cost per km, is removed after asking. */
export function MileageReadingsSection({ asset }: { asset: AssetView }) {
  const { t } = useTranslation();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const readings = [...(asset.mileageReadings ?? [])].reverse();
  const writable = asset.canWrite;
  return (
    <section aria-labelledby="mileage-readings-title" className="space-y-3">
      <h3 id="mileage-readings-title" ref={headingRef} tabIndex={-1} className="font-medium">{t('assets_mileage.title')}</h3>
      <Table aria-label={t('assets_mileage.title')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('assets_mileage.col_date')}</TableHead>
            <TableHead className="text-right">{t('assets_mileage.col_km')}</TableHead>
            {writable ? <TableHead className="w-0"><span className="sr-only">{t('assets_mileage.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {readings.length === 0 ? (
            <TableRow><TableCell colSpan={writable ? 3 : 2} className="text-muted-foreground">{t('assets_mileage.empty')}</TableCell></TableRow>
          ) : readings.map((r) => (
            <TableRow key={r.on}>
              <TableCell><LocalDate value={r.on} /></TableCell>
              <TableCell className="text-right tabular-nums">{t('assets_detail.km', { value: r.km.toLocaleString('fr-FR') })}</TableCell>
              {writable ? (
                <TableCell>
                  <DeleteReading asset={asset} on={r.on} onDeleted={(m) => { toast.success(m); headingRef.current?.focus(); }} onReloaded={() => headingRef.current?.focus()} />
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </section>
  );
}

function DeleteReading({ asset, on, onDeleted, onReloaded }: { asset: AssetView; on: string; onDeleted: (message: string) => void; onReloaded: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', ASSET_MILEAGE_READING, {
    onSuccess: () => { void invalidateAssets(queryClient); onDeleted(t('assets_mileage.deleted')); },
  });
  return (
    <RowActions menuId={`mileage-reading-${on}`} menuLabel={t('assets_mileage.row_menu', { date: formatLocalDate(on) })}
      items={[]} confirmation={t('assets_mileage.delete_confirm')} remove={remove} onReloaded={onReloaded}
      present={presentConflict} invalidate={invalidateAssets}
      onConfirm={() => remove.mutate({ params: { path: { id: asset.id, on } } })} />
  );
}
