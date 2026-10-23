import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { TAX_SALE } from './api';
import { SaleItem } from './SaleItem';
import '@/locales/tax';

/** « Impôt si vente aujourd'hui » of a property or an envelope: absent when the item has no page (closed, not visible). */
export function SaleTaxSection({ item, canWrite }: { item: string; canWrite: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', TAX_SALE, { params: { query: { item } } });
  if (answer.error) {
    const problem = toProblem(answer.error);
    return problem.status === 404 ? null : <Section title={t('tax.sale.title')}><ProblemMessage problem={present(problem)} /></Section>;
  }
  const view = answer.data;
  const first = view?.items[0];
  if (!view) return <div role="status" aria-busy="true" aria-label={t('tax.loading')} className="h-24 animate-pulse rounded-md border bg-muted" />;
  if (!first) return null;
  return (
    <Section title={t('tax.sale.title')}>
      <SaleItem item={first} carriedForward={view.carriedForward} canWrite={canWrite} />
    </Section>
  );
}
