import { Link, useParams } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { AccessBadge } from '@/components/AccessBadge';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Section } from '../income/Section';
import { PROPERTY } from './api';
import { LinkedLoans } from './detail/LinkedLoans';
import { PropertyFigures } from './detail/PropertyFigures';
import { RentalSection } from './rental/RentalSection';
import { FlowsSection } from './flows/FlowsSection';
import { typeText, warningText } from './format';
import { SettingsSection } from './settings/SettingsForm';
import { ValuationsSection } from './valuations/ValuationsSection';

/** 3D1 design §6: a property — its figures today, warnings, linked loans, estimates, flows and settings. */
export function PropertyDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/properties/$id' });
  const answer = $api.useQuery('get', PROPERTY, { params: { path: { id } } });
  const property = answer.data;

  return (
    <article className="space-y-6">
      <Link to="/properties" className="text-sm underline">{t('property_detail.back')}</Link>
      {answer.error ? (
        // An unknown property and another adult's private one both answer 404: « Cet élément est introuvable. ».
        <ProblemMessage problem={present(toProblem(answer.error))} />
      ) : !property ? (
        <div aria-busy="true" aria-label={t('property_detail.loading')} className="h-40 animate-pulse rounded-md border bg-muted" />
      ) : (
        <>
          <header className="flex flex-wrap items-center gap-3">
            <h1 className="text-2xl font-semibold">{property.name}</h1>
            <span className="text-sm text-muted-foreground">{typeText(t, property.type)}</span>
            {property.locationLabel ? <span className="text-sm text-muted-foreground">{property.locationLabel}</span> : null}
            <AccessBadge access={property.visibility === 'private' ? 'private' : 'shared'} />
          </header>
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
          <Section title={t('property_detail.figures')}><PropertyFigures property={property} /></Section>
          <Section title={t('property_detail.loans')}><LinkedLoans property={property} /></Section>
          <RentalSection property={property} />
          <ValuationsSection property={property} />
          <FlowsSection property={property} />
          {property.canWrite ? <SettingsSection property={property} /> : null}
        </>
      )}
    </article>
  );
}
