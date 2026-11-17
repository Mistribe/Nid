import { useTranslation } from 'react-i18next';
import type { TaxEstimate } from '@/api/tax';
import { Section } from '@/components/page/Section';
import { SaleTotalSection } from '../SaleTotalSection';
import { SimulationForm } from '../SimulationForm';
import '@/locales/tax';

/** Edited inputs against the estimate, then everything sold. */
export function SimulationTab({ estimate, scope }: { estimate: TaxEstimate; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  return (
    <>
      <SimulationForm key={estimate.year} estimate={estimate} />
      <Section title={t('tax.tab.sale')}><SaleTotalSection scope={scope} /></Section>
    </>
  );
}
