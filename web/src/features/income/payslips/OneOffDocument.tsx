import { useTranslation } from 'react-i18next';
import type { DocumentSummary } from '@/api/documents';
import type { IncomeOneOff, IncomeSourceDetail } from '@/api/income';
import { oneOffDocuments, oneOffTitle } from './payslip';
import { PayslipLink, PayslipMenu, PayslipProblem } from './PayslipCell';
import { usePayslipWrites } from './usePayslipWrites';
import '@/locales/income';

/** A one-off's documents among the source's anchored `docs`: a paperclip each, with the payslips' menu. « Retirer » unlinks the one-off only. */
export function OneOffDocument({ source, oneOff, docs }: { source: IncomeSourceDetail; oneOff: IncomeOneOff; docs: DocumentSummary[] }) {
  const { t } = useTranslation();
  const writes = usePayslipWrites();
  const mine = oneOffDocuments(docs, oneOff.id);
  if (mine.length === 0) return null;
  const replaceWith = { target: { kind: 'income.one_off', id: oneOff.id }, date: oneOff.occurredOn, title: oneOffTitle(t, oneOff) };
  return (
    <div className="space-y-1">
      <ul className="space-y-1">
        {mine.map((doc) => (
          <li key={doc.id} className="flex items-center gap-1">
            <PayslipLink doc={doc} />
            <PayslipMenu doc={doc} writes={writes} writable={source.canWrite} replaceWith={replaceWith}
              links={doc.links.filter((l) => l.targetKind === 'income.one_off' && l.targetId === oneOff.id)} />
          </li>
        ))}
      </ul>
      <PayslipProblem writes={writes} />
    </div>
  );
}
