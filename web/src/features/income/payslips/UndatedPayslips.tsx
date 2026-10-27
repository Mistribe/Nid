import { Link } from '@tanstack/react-router';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import type { DocumentSummary } from '../../documents/api';
import { PayslipLink } from './PayslipCell';
import '@/locales/income';

/** The source's payslips without a document date (they cover no month), each with its document page to date it. */
export function UndatedPayslips({ docs }: { docs: DocumentSummary[] }) {
  const { t } = useTranslation();
  const id = useId();
  if (docs.length === 0) return null;
  return (
    <div className="space-y-1">
      <h3 id={id} className="font-medium">{t('income_detail.payslips.undated')}</h3>
      <ul aria-labelledby={id} className="space-y-1">
        {docs.map((doc) => (
          <li key={doc.id} className="flex flex-wrap items-center gap-3">
            <PayslipLink doc={doc} />
            <Link to="/documents/$id" params={{ id: doc.id }} className="text-sm text-muted-foreground underline-offset-4 hover:underline">
              {t('income_detail.payslips.open')}
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}
