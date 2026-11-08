import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle, Plus } from 'lucide-react';
import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { PageTrail } from '@/components/page/trail';
import { SharingBadges } from '@/components/ownership/SharingBadges';
import { SharingMenu } from '@/components/ownership/SharingMenu';
import { ErrorPage } from '@/components/ErrorPage';
import { Button } from '@/components/ui/button';
import { Section } from '@/components/page/Section';
import { PROPERTY } from '@/api/properties';
import { CostsSection } from './costs/CostsSection';
import { LinkedLoans } from './detail/LinkedLoans';
import { PropertyCostDetail, PropertyStats } from './detail/PropertyFigures';
import { FlowsSection } from './flows/FlowsSection';
import { typeText, warningText } from './format';
import { RentalSection } from './rental/RentalSection';
import { SettingsSection, SettingsSubmit } from './settings/SettingsForm';
import { ValuationDialog } from './valuations/ValuationForm';
import { ValuationsSection } from './valuations/ValuationsSection';
import { SaleTaxSection } from '@/features/tax/SaleTaxSection';
import '@/locales/properties';
import { ItemDocuments } from '@/features/documents/ItemDocuments';
import '@/locales/documents';
import { PageSkeleton } from '@/components/page/skeletons';

const TABS = ['overview', 'rental', 'return', 'expenses', 'valuations', 'documents', 'settings'] as const;

// Property return spec §6: loaded with its tab.
const ReturnSection = lazy(() => import('./return/ReturnSection').then((m) => ({ default: m.ReturnSection })));

/**
 * 3D1 design §6, redesign §5.2–5.3: a property as a tabbed page. Under the title, its figures today (and the rental ones
 * when it is rented); the overview adds the real cost's detail and the linked loans; then the rental (only for a rental or
 * a property with a tenancy), the expenses (flows and planned charges), the estimates, the documents and the settings. Estimating is the header's action: its dialog, its toast
 * and the focus it returns to are the page's; every other table owns its dialog.
 */
export function PropertyDetailPage() {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const { $api } = useApi();
  const { id } = useParams({ from: '/properties/$id' });
  const answer = $api.useQuery('get', PROPERTY, { params: { path: { id } } });
  const [tab, setTab] = useDetailTab(TABS);
  const [estimating, setEstimating] = useState(false);
  const estimateRef = useRef<HTMLButtonElement>(null);
  const [focusEstimate, setFocusEstimate] = useState(false);
  useEffect(() => {
    if (!focusEstimate) return;
    estimateRef.current?.focus();
    setFocusEstimate(false);
  }, [focusEstimate]);
  const property = answer.data;
  // No `PageHeader` yet: the bar's trail leads back to the list (no inline link of its own).
  const crumbs = [{ label: t('nav.properties'), to: '/properties' as const }];
  const trail = <PageTrail crumbs={crumbs} />;

  if (answer.error) {
    // An unknown property and another adult's private one both answer 404: the same « Page introuvable ».
    return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  }
  if (!property) return <article className="space-y-6">{trail}<PageSkeleton label={t('property_detail.loading')} actions={2} stats={4} tabs={5} /></article>;

  const rented = property.type === 'rental' || property.tenancies.length > 0;
  const shown = (tab === 'settings' && !property.canWrite) || ((tab === 'rental' || tab === 'return') && !rented) ? 'overview' : tab;
  const actions = (
    <>
      {property.canWrite ? (
        <Button ref={estimateRef} type="button" onClick={() => setEstimating(true)}>
          <Plus aria-hidden="true" />{t('property_valuations.add')}
        </Button>
      ) : null}
      <SharingMenu item={id} onAnnounce={toast.success} shared={property.canWrite && property.visibility === 'shared'} />
    </>
  );

  return (
    <article className="space-y-6">
      <PageHeader crumbs={crumbs} title={property.name} actions={actions}
        meta={(
          <>
            <span className="text-sm text-muted-foreground">{typeText(t, property.type)}</span>
            {property.locationLabel ? <span className="text-sm text-muted-foreground">{property.locationLabel}</span> : null}
            <AccessBadge access={property.visibility === 'private' ? 'private' : 'shared'} /><SharingBadges item={id} />
            {property.type === 'rental' && capabilities.modules.includes('income') ? (
              <Link to="/tax" search={{ tab: '2044' }} className="text-sm hover:underline">{t('property_detail.tax_2044')}</Link>
            ) : null}
          </>
        )}>
        <section aria-label={t('property_detail.figures')} className="space-y-2"><PropertyStats property={property} /></section>
      </PageHeader>
      {property.canWrite ? null : <p>{t('property_detail.read_only')}</p>}
      {property.warnings.length > 0 ? (
        <ul aria-label={t('property_detail.warnings')} className="space-y-1">
          {property.warnings.map((w, i) => (
            <li key={`${w.code}-${w.loanId ?? ''}-${i}`} className="flex items-center gap-2">
              <AlertTriangle aria-hidden="true" className="size-4 text-status-warning" />
              {warningText(t, w, property)}
            </li>
          ))}
        </ul>
      ) : null}
      {property.canWrite && estimating ? (
        <ValuationDialog property={property}
          onClose={() => { setEstimating(false); setFocusEstimate(true); }}
          onDone={(message) => { setEstimating(false); toast.success(message); setFocusEstimate(true); }} />
      ) : null}
      <DetailTabs value={shown} onChange={setTab} tabs={[
        {
          id: 'overview', label: t('property_detail.tab.overview'),
          content: (
            <>
              <Section title={t('property_detail.cost_title')}><PropertyCostDetail property={property} /></Section>
              <SaleTaxSection item={property.id} canWrite={property.canWrite} />
              <Section title={t('property_detail.loans')}><LinkedLoans property={property} /></Section>
            </>
          ),
        },
        ...(rented ? [
          { id: 'rental' as const, label: t('property_detail.tab.rental'), content: <RentalSection property={property} /> },
          { id: 'return' as const, label: t('property_detail.tab.return'), content: <Suspense fallback={null}><ReturnSection property={property} /></Suspense> },
        ] : []),
        { id: 'expenses', label: t('property_detail.tab.expenses'), content: <><FlowsSection property={property} /><CostsSection property={property} /></> },
        { id: 'valuations', label: t('property_detail.tab.valuations'), content: <ValuationsSection property={property} /> },
        { id: 'documents' as const, label: t('documents_item.title'), content: <ItemDocuments itemId={property.id} canWrite={property.canWrite} defaultRole="deed" /> },
        ...(property.canWrite ? [{ id: 'settings' as const, label: t('property_detail.tab.settings'), content: <SettingsSection property={property} />, action: <SettingsSubmit /> }] : []),
      ]} />
    </article>
  );
}
