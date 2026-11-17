import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { TaxEstimate, TaxResult } from '@/api/tax';
import { Section } from '@/components/page/Section';
import { SaleTotalSection } from '../SaleTotalSection';
import { SimulationForm } from './SimulationForm';
import { SimulationResult } from './SimulationResult';
import '@/locales/tax';

/** Edited inputs against the estimate, then everything sold. */
export function SimulationTab({ estimate, scope }: { estimate: TaxEstimate; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const [result, setResult] = useState<TaxResult | null>(null);
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('tax.simulation.question')}</p>
      {result ? <SimulationResult before={estimate.result} after={result} year={estimate.year} /> : null}
      <SimulationForm key={estimate.year} estimate={estimate} scope={scope} onResult={setResult} />
      <Section title={t('tax.tab.sale')}><SaleTotalSection scope={scope} /></Section>
    </>
  );
}
