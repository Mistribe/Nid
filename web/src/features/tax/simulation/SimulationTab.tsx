import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { TaxEstimate, TaxResult } from '@/api/tax';
import { Section } from '@/components/page/Section';
import { ResultTable } from '../ResultTable';
import { SaleTotalSection } from '../SaleTotalSection';
import { SimulationForm } from './SimulationForm';
import '@/locales/tax';

/** Edited inputs against the estimate, then everything sold. */
export function SimulationTab({ estimate, scope }: { estimate: TaxEstimate; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const [result, setResult] = useState<TaxResult | null>(null);
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('tax.simulation.question')}</p>
      <SimulationForm key={estimate.year} estimate={estimate} scope={scope} onResult={setResult} />
      {result ? (
        <ResultTable label={t('tax.simulation.result')}
          columns={[{ title: t('tax.estimate'), result: estimate.result }, { title: t('tax.simulation.column'), result }]} />
      ) : null}
      <Section title={t('tax.tab.sale')}><SaleTotalSection scope={scope} /></Section>
    </>
  );
}
