import { useQueryClient } from '@tanstack/react-query';
import { useParams } from '@tanstack/react-router';
import { Plus } from 'lucide-react';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { ASSET, ASSET_ARCHIVE, ASSET_UNARCHIVE, invalidateAssets } from '@/api/assets';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { ErrorPage } from '@/components/ErrorPage';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { SharingBadges } from '@/components/ownership/SharingBadges';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { PageSkeleton } from '@/components/page/skeletons';
import { StatLine } from '@/components/page/StatLine';
import { PageTrail } from '@/components/page/trail';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { ItemDocuments } from '@/features/documents/ItemDocuments';
import { EditAssetDialog } from './EditAssetDialog';
import { energyText, kindText } from './format';
import { MileageDialog } from './MileageDialog';
import { AssetCostsSection } from './costs/AssetCostsSection';
import { AssetSubscriptionsSection } from './subscriptions/AssetSubscriptionsSection';
import { AssetValuationDialog } from './valuations/AssetValuationForm';
import { AssetValuationsSection } from './valuations/AssetValuationsSection';
import '@/locales/assets';
import '@/locales/deletion';
import '@/locales/documents';

const DeleteItemFlow = lazy(() => import('@/components/DeleteItemFlow'));
const TABS = ['valuations', 'costs', 'subscriptions', 'documents'] as const;

/**
 * An asset: its value inline under the title (with « prix d’achat » while the purchase price stands in for a valuation), its
 * purchase and, for a metal, its weight and fineness, for a vehicle its registration, mileage, horsepower and energy; then the values and the documents. Editing, archiving and deleting are the header's.
 */
export function AssetDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const { id } = useParams({ from: '/other-assets/$id' });
  const answer = $api.useQuery('get', ASSET, { params: { path: { id } } });
  const [tab, setTab] = useDetailTab(TABS);
  const [dialog, setDialog] = useState<'edit' | 'value' | 'mileage' | 'delete' | null>(null);
  const done = () => { void invalidateAssets(queryClient); };
  const archive = $api.useMutation('post', ASSET_ARCHIVE, { onSuccess: () => { done(); toast.success(t('assets_detail.archived_done')); } });
  const unarchive = $api.useMutation('post', ASSET_UNARCHIVE, { onSuccess: () => { done(); toast.success(t('assets_detail.unarchived_done')); } });
  const asset = answer.data;
  const crumbs = [{ label: t('nav.assets'), to: '/other-assets' as const }];

  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!asset) return <article className="space-y-6"><PageTrail crumbs={crumbs} /><PageSkeleton label={t('assets_detail.loading')} actions={2} stats={4} tabs={3} /></article>;

  const toggle = asset.archived ? unarchive : archive;
  const actions = (
    <>
      {asset.canWrite ? (
        <>
          <Button type="button" onClick={() => setDialog('value')}><Plus aria-hidden="true" />{t('assets_valuations.add')}</Button>
          <Button type="button" variant="outline" onClick={() => setDialog('edit')}>{t('assets_form.edit')}</Button>
        </>
      ) : null}
      <SharingMenu item={id} onAnnounce={toast.success} shared={asset.canWrite && asset.visibility === 'shared'} label={t('page.more_actions')}
        trailing={asset.canWrite ? [
          ...(asset.kind === 'vehicle' ? [{ label: t('assets_form.update_mileage'), onSelect: () => setDialog('mileage') }] : []),
          { label: t(asset.archived ? 'assets_detail.unarchive' : 'assets_detail.archive'), onSelect: () => { if (!toggle.isPending) toggle.mutate({ params: { path: { id } } }); } },
          { label: t('deletion.menu'), destructive: true, onSelect: () => setDialog('delete') },
        ] : []} />
    </>
  );
  const figures = [
    { label: t('assets_detail.value'),
      hint: asset.valueEstimated && asset.valueOn
        ? <>{t(asset.valueFromPurchase ? 'assets_detail.estimated_from_purchase' : 'assets_detail.estimated_from_value')} <LocalDate value={asset.valueOn} />{asset.valueBase ? <> (<Money value={asset.valueBase} />)</> : null}</>
        : asset.valueOn ? <LocalDate value={asset.valueOn} /> : undefined,
      value: asset.value ? <span className="inline-flex flex-wrap items-center gap-2"><Money value={asset.value} />{asset.valueEstimated ? <Badge variant="secondary">{t('assets_detail.estimated')}</Badge> : asset.valueFromPurchase ? <Badge variant="secondary">{t('assets_detail.from_purchase')}</Badge> : null}</span> : '—' },
    ...(asset.depreciationRate != null ? [{ label: t('assets_detail.depreciation'), value: t('assets_detail.per_year', { value: asset.depreciationRate.toLocaleString('fr-FR') }) }] : []),
    ...(asset.purchasedOn ? [{ label: t('assets_detail.purchased_on'), value: <LocalDate value={asset.purchasedOn} /> }] : []),
    ...(asset.purchasePrice ? [{ label: t('assets_detail.purchased_for'), value: <Money value={asset.purchasePrice} /> }] : []),
    ...(asset.weightGrams != null ? [{ label: t('assets_detail.weight'), value: t('assets_detail.grams', { value: asset.weightGrams.toLocaleString('fr-FR') }) }] : []),
    ...(asset.fineness != null ? [{ label: t('assets_detail.fineness'), value: t('assets_detail.per_mille', { value: asset.fineness.toLocaleString('fr-FR') }) }] : []),
    ...(asset.registeredOn ? [{ label: t('assets_detail.registered_on'), value: <LocalDate value={asset.registeredOn} /> }] : []),
    ...(asset.mileageKm != null ? [{ label: t('assets_detail.mileage'), hint: asset.mileageOn ? <LocalDate value={asset.mileageOn} /> : undefined, value: t('assets_detail.km', { value: asset.mileageKm.toLocaleString('fr-FR') }) }] : []),
    ...(asset.fiscalHp != null ? [{ label: t('assets_detail.fiscal_hp'), value: t('assets_detail.cv', { value: asset.fiscalHp }) }] : []),
    ...(asset.energy ? [{ label: t('assets_detail.energy'), value: energyText(t, asset.energy) }] : []),
  ];

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} title={asset.name} actions={actions}
        meta={(
          <>
            <span className="text-sm text-muted-foreground">{kindText(t, asset.kind)}</span>
            <AccessBadge access={asset.visibility === 'private' ? 'private' : 'shared'} /><SharingBadges item={id} />
            {asset.archived ? <Badge variant="secondary">{t('assets_detail.archived')}</Badge> : null}
            <OwnerChips owners={asset.owners} />
          </>
        )}>
        <section aria-label={t('assets_detail.figures')}><StatLine items={figures} /></section>
      </PageHeader>
      {asset.canWrite ? null : <p>{t('assets_detail.read_only')}</p>}
      {archive.error || unarchive.error ? <ProblemMessage problem={present(toProblem(archive.error ?? unarchive.error))} /> : null}
      {asset.note ? <p>{asset.note}</p> : null}
      {asset.canWrite && dialog === 'delete' ? <Suspense fallback={null}><DeleteItemFlow kind="assets.delete_asset" id={id} onClose={() => setDialog(null)} /></Suspense> : null}
      {asset.canWrite && dialog === 'edit' ? <EditAssetDialog asset={asset} onClose={() => setDialog(null)} onDone={(m) => { setDialog(null); toast.success(m); }} /> : null}
      {asset.canWrite && dialog === 'mileage' ? <MileageDialog asset={asset} onClose={() => setDialog(null)} onDone={(m) => { setDialog(null); toast.success(m); }} /> : null}
      {asset.canWrite && dialog === 'value' ? <AssetValuationDialog asset={asset} onClose={() => setDialog(null)} onDone={(m) => { setDialog(null); toast.success(m); }} /> : null}
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'valuations', label: t('assets_detail.tab_valuations'), content: <AssetValuationsSection asset={asset} /> },
        // #649: the running costs are a vehicle's; another asset shows them only while it still holds some (history).
        ...(asset.kind === 'vehicle' || asset.hasCosts ? [{ id: 'costs' as const, label: t('assets_costs.tab'), content: <AssetCostsSection asset={asset} /> }] : []),
        { id: 'subscriptions', label: t('assets_subscriptions.tab'), content: <AssetSubscriptionsSection asset={asset} /> },
        { id: 'documents', label: t('documents_item.title'), content: <ItemDocuments itemId={asset.id} canWrite={asset.canWrite} defaultRole="invoice" /> },
      ]} />
    </article>
  );
}
