import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle, Plus } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Section } from '@/components/page/Section';
import { PROPERTY } from './api';
import { CostsSection } from './costs/CostsSection';
import { LinkedLoans } from './detail/LinkedLoans';
import { PropertyCostDetail, PropertyStats } from './detail/PropertyFigures';
import { FlowsSection } from './flows/FlowsSection';
import { typeText, warningText } from './format';
import { RentalSection } from './rental/RentalSection';
import { SettingsSection } from './settings/SettingsForm';
import { ValuationDialog } from './valuations/ValuationForm';
import { ValuationsSection } from './valuations/ValuationsSection';
import '@/locales/properties';

const TABS = ['overview', 'rental', 'valuations', 'expenses', 'settings'] as const;

/**
 * 3D1 design §6, redesign §5.2–5.3: a property as a tabbed page. Under the title, its figures today (and the rental ones
 * when it is rented); the overview adds the real cost's detail and the linked loans; then the rental, the estimates, the
 * expenses (flows and planned charges) and the settings. Estimating is the header's action: its dialog, its announcement
 * and the focus it returns to are the page's; every other table owns its dialog.
 */
export function PropertyDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/properties/$id' });
  const answer = $api.useQuery('get', PROPERTY, { params: { path: { id } } });
  const [tab, setTab] = useDetailTab(TABS);
  const [estimating, setEstimating] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const estimateRef = useRef<HTMLButtonElement>(null);
  const [focusEstimate, setFocusEstimate] = useState(false);
  useEffect(() => {
    if (!focusEstimate) return;
    estimateRef.current?.focus();
    setFocusEstimate(false);
  }, [focusEstimate]);
  const property = answer.data;
  const back = <Link to="/properties" className="text-sm underline">{t('property_detail.back')}</Link>;
  // Mounted from the first render, loading included, so the text written later is announced.
  const status = <p role="status" aria-live="polite">{announcement}</p>;

  if (answer.error) {
    // An unknown property and another adult's private one both answer 404: « Cet élément est introuvable. ».
    return <article className="space-y-6">{back}{status}<ProblemMessage problem={present(toProblem(answer.error))} /></article>;
  }
  if (!property) return <article className="space-y-6">{back}{status}<div aria-busy="true" aria-label={t('property_detail.loading')} className="h-40 animate-pulse rounded-md border bg-muted" /></article>;

  const shown = property.canWrite || tab !== 'settings' ? tab : 'overview';
  const actions = property.canWrite ? (
    <Button ref={estimateRef} type="button" onClick={() => { setAnnouncement(''); setEstimating(true); }}>
      <Plus aria-hidden="true" />{t('property_valuations.add')}
    </Button>
  ) : null;

  return (
    <article className="space-y-6">
      <PageHeader crumbs={[{ label: t('nav.properties'), to: '/properties' }]} title={property.name} actions={actions}
        meta={(
          <>
            <span className="text-sm text-muted-foreground">{typeText(t, property.type)}</span>
            {property.locationLabel ? <span className="text-sm text-muted-foreground">{property.locationLabel}</span> : null}
            <AccessBadge access={property.visibility === 'private' ? 'private' : 'shared'} />
          </>
        )}>
        <section aria-label={t('property_detail.figures')} className="space-y-2"><PropertyStats property={property} /></section>
      </PageHeader>
      {status}
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
          onDone={(message) => { setEstimating(false); setAnnouncement(message); setFocusEstimate(true); }} />
      ) : null}
      <DetailTabs value={shown} onChange={setTab} tabs={[
        {
          id: 'overview', label: t('property_detail.tab.overview'),
          content: (
            <>
              <Section title={t('property_detail.cost_title')}><PropertyCostDetail property={property} /></Section>
              <Section title={t('property_detail.loans')}><LinkedLoans property={property} /></Section>
            </>
          ),
        },
        { id: 'rental', label: t('property_detail.tab.rental'), content: <RentalSection property={property} /> },
        { id: 'valuations', label: t('property_detail.tab.valuations'), content: <ValuationsSection property={property} /> },
        { id: 'expenses', label: t('property_detail.tab.expenses'), content: <><FlowsSection property={property} /><CostsSection property={property} /></> },
        ...(property.canWrite ? [{ id: 'settings' as const, label: t('property_detail.tab.settings'), content: <SettingsSection property={property} /> }] : []),
      ]} />
    </article>
  );
}
