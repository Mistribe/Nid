import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { notEditable, type KindUi } from './kindParts';
import '@/locales/proposals';

type CategorisePayload = { lines: { transactionId: string; categoryId: string }[] };
type CategorisePreview = {
  count?: number;
  hidden?: number;
  lines?: { transactionId: string; bookedOn: string; label: string; accountName: string; amount: string; currency: string; current: string | null; proposed: string | null; applies: boolean }[];
};

/** Spec §Hints and the assistant: the lines the viewer reads, each with its category now and the one proposed, as the server gives them. */
function CategorisePreviewView({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const { count = 0, hidden = 0, lines = [] } = preview as CategorisePreview;
  const none = t('proposal.preview.none');
  return (
    <div className="space-y-3">
      <p>{t('proposal.categorise.count', { count })}{hidden > 0 ? ` ${t('proposal.categorise.hidden', { count: hidden })}` : ''}</p>
      <Table>
        <caption className="sr-only">{t('proposal.preview.title')}</caption>
        <TableHeader>
          <TableRow>
            <TableHead>{t('proposal.categorise.date')}</TableHead>
            <TableHead>{t('proposal.categorise.label')}</TableHead>
            <TableHead>{t('proposal.categorise.account')}</TableHead>
            <TableHead className="text-right">{t('proposal.categorise.amount')}</TableHead>
            <TableHead>{t('proposal.categorise.current')}</TableHead>
            <TableHead>{t('proposal.categorise.proposed')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {lines.map((l) => (
            <TableRow key={l.transactionId}>
              <TableCell>{formatLocalDate(l.bookedOn)}</TableCell>
              <TableCell>{l.label}</TableCell>
              <TableCell>{l.accountName}</TableCell>
              <TableCell className="text-right tabular-nums">{formatMoney(l)}</TableCell>
              <TableCell>{l.current ?? none}</TableCell>
              <TableCell>
                {l.proposed ?? none}
                {l.applies ? null : <span className="block text-sm text-muted-foreground">{t('proposal.categorise.skipped')}</span>}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

export const categorise: KindUi = {
  label: 'proposals.kind.categorise',
  summary: (t, p) => t('proposals.summary.categorise', { count: ((p.editedPayload ?? p.payload) as CategorisePayload).lines.length }),
  Preview: CategorisePreviewView,
  ...notEditable((t) => t('proposal.categorise.as_proposed')),
  targetless: true,
  link: () => undefined,
  // appliedRefs holds the lines categorised; the payload's others were skipped. Counts of lines, not amounts.
  outcome: (t, p) => {
    const total = ((p.editedPayload ?? p.payload) as CategorisePayload).lines.length;
    return `${t('proposal.categorise.applied', { count: p.appliedRefs.length })}, ${t('proposal.categorise.unchanged', { count: total - p.appliedRefs.length })}`;
  },
};
