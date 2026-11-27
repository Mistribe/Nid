import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ASSET_COST, ASSET_COSTS, invalidateAssets, type AssetCost, type AssetView } from '@/api/assets';
import { present, toProblem } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { RowActions } from '@/components/page/RowActions';
import { Section } from '@/components/page/Section';
import { useRowDialog } from '@/components/page/useRowDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { SourceLink } from '@/features/documents/SourceLink';
import { presentRefusal } from '@/features/loans/refusal';
import { AssetCostDialog } from './AssetCostForm';
import { categoryText } from './cost';
import { MileageReadingsSection } from './MileageReadingsSection';
import { OwnershipCostSection } from './OwnershipCostSection';
import '@/locales/assets';
import '@/locales/loans';

/**
 * #649: a vehicle's running costs of one year, newest first, with the year's total (the server's). Adding and correcting open a
 * dialog; a cost taken from a bank line says so. Only a vehicle's writer adds, corrects or removes.
 */
export function AssetCostsSection({ asset, onTab }: { asset: AssetView; onTab: (tab: 'subscriptions' | 'valuations') => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [year, setYear] = useState<number | undefined>();
  const answer = $api.useQuery('get', ASSET_COSTS, { params: { path: { id: asset.id }, query: { year } } });
  const costs = answer.data;
  const rows = costs?.rows ?? [];
  const s = useRowDialog(rows, 'cost-menu');
  const writable = asset.canWrite && asset.kind === 'vehicle';

  return (
    <Section title={t('assets_costs.title')} headingRef={s.headingRef}>
      {asset.kind === 'vehicle' ? <OwnershipCostSection asset={asset} onTab={onTab} /> : null}
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      <div className="flex flex-wrap items-end justify-between gap-3">
        {costs ? (
          <div className="flex flex-wrap items-center gap-3">
            <div className="w-32">
              <Field label={t('assets_costs.year')}>
                {(c) => <SelectField {...c} value={String(costs.year)} onValueChange={(v) => setYear(Number(v))}
                  options={costs.years.map((y) => ({ value: String(y), label: String(y) }))} />}
              </Field>
            </div>
            <p className="text-sm">{t('assets_costs.total', { year: costs.year })} <strong className="tabular-nums"><Money value={costs.total} /></strong></p>
          </div>
        ) : <span />}
        {writable ? <Button ref={s.addButtonRef} type="button" variant="outline" size="sm" onClick={() => s.open()}>{t('assets_costs.add')}</Button> : null}
      </div>
      {writable && s.dialog ? <AssetCostDialog asset={asset} editing={s.dialog.editing} onClose={s.cancel} onDone={s.saved} /> : null}
      {asset.kind !== 'vehicle' ? <p className="text-sm text-muted-foreground">{t('assets_costs.not_vehicle')}</p> : null}
      <Table aria-label={t('assets_costs.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('assets_costs.col_date')}</TableHead>
            <TableHead>{t('assets_costs.category')}</TableHead>
            <TableHead className="text-right">{t('assets_costs.amount')}</TableHead>
            <TableHead>{t('assets_costs.col_note')}</TableHead>
            {asset.canWrite ? <TableHead className="w-0"><span className="sr-only">{t('assets_costs.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.length === 0 ? (
            <TableRow><TableCell colSpan={asset.canWrite ? 5 : 4} className="text-muted-foreground">{t('assets_costs.empty')}</TableCell></TableRow>
          ) : rows.map((c) => (
            <TableRow key={c.id}>
              <TableCell><LocalDate value={c.occurredOn} /></TableCell>
              <TableCell>{categoryText(t, c.category)}</TableCell>
              <TableCell className="text-right tabular-nums"><Money value={c.amount} /></TableCell>
              <TableCell className="whitespace-normal">
                {c.note}
                <SourceLink documentId={c.documentId} />
                {c.fromBank ? <Badge variant="secondary" className="ml-2">{t('assets_costs.from_bank')}</Badge> : null}
              </TableCell>
              {asset.canWrite ? (
                <TableCell>
                  <CostRowActions asset={asset} cost={c} onEdit={() => s.open(c)} onDeleted={s.announce} onReloaded={() => s.headingRef.current?.focus()} />
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {asset.kind === 'vehicle' ? <MileageReadingsSection asset={asset} /> : null}
    </Section>
  );
}

/** « Corriger » opens the dialog (only on a vehicle); « Supprimer » asks first, and stays for a cost of an asset that stopped being a vehicle. */
function CostRowActions({ asset, cost, onEdit, onDeleted, onReloaded }: {
  asset: AssetView; cost: AssetCost; onEdit: () => void; onDeleted: (message: string) => void; onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', ASSET_COST, {
    onSuccess: () => { void invalidateAssets(queryClient); onDeleted(t('assets_costs.deleted')); },
  });
  return (
    <RowActions menuId={`cost-menu-${cost.id}`} menuLabel={t('assets_costs.row_menu', { date: formatLocalDate(cost.occurredOn), what: categoryText(t, cost.category) })}
      items={asset.kind === 'vehicle' ? [{ label: t('assets_costs.correct'), onSelect: onEdit }] : []} confirmation={t('assets_costs.delete_confirm')}
      remove={remove} onReloaded={onReloaded} present={presentRefusal} invalidate={invalidateAssets}
      onConfirm={() => remove.mutate({ params: { path: { id: asset.id, flowId: cost.id } } })} />
  );
}
