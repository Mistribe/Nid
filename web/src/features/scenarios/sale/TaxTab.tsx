import { useTranslation } from 'react-i18next';
import { Section } from '@/components/page/Section';
import { SaleItem } from '../../tax/SaleItem';
import type { SaleResult } from '../api';
import '@/locales/scenarios';

/** « Impôt »: the property's page of « Impôt si vente », sold on the scenario's date for its price less the agency fees. */
export default function TaxTab({ result }: { result: SaleResult }) {
  const { t } = useTranslation();
  return (
    <Section title={t('scenarios.sale.tax')}>
      {result.tax ? <SaleItem item={result.tax} carriedForward={false} /> : <p className="text-sm text-muted-foreground">{t('scenarios.sale.no_tax')}</p>}
    </Section>
  );
}
