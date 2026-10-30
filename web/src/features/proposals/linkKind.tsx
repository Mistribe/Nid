import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { Table, TableBody } from '@/components/ui/table';
import { Row, notEditable, type KindUi } from './kindParts';
import '@/locales/proposals';

type LinkPayload = { transactionId: string; module: string; itemId: string; key?: string | null };
type LinkPreview = {
  transaction?: { bookedOn: string; label: string; amount: string; currency: string };
  charge?: { itemName: string; dueOn: string; amount: string; currency: string } | null;
  gap?: string | null;
};

/** 5C §6.3 step 4: the line, the charge it pays and the gap, all as the server gives them. */
function LinkPreviewView({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const { transaction: line, charge, gap } = preview as LinkPreview;
  const none = t('proposal.preview.none');
  return (
    <Table>
      <caption className="sr-only">{t('proposal.preview.title')}</caption>
      <TableBody>
        <Row label={t('proposal.link.transaction')}>
          {line ? `${formatLocalDate(line.bookedOn)} · ${line.label} · ${formatMoney(line)}` : none}
        </Row>
        <Row label={t('proposal.link.charge')}>{charge ? `${charge.itemName} · ${formatLocalDate(charge.dueOn)} · ${formatMoney(charge)}` : none}</Row>
        <Row label={t('proposal.link.gap')}>{gap != null && line ? formatMoney({ amount: gap, currency: line.currency }) : none}</Row>
      </TableBody>
    </Table>
  );
}

export const link: KindUi = {
  label: 'proposals.kind.link',
  summary: (t, p) => t('proposals.summary.link', { name: p.targetName ?? t('proposals.unknown_target') }),
  Preview: LinkPreviewView,
  ...notEditable((t) => t('proposal.link.as_proposed')),
  link: (p) => {
    const { module, key } = (p.editedPayload ?? p.payload) as LinkPayload;
    const id = p.targetItemId ?? '';
    if (module === 'subscriptions') return { to: '/subscriptions/$id', params: { id } };
    // A real-estate charge's key names a property's cost or rent; a loan instalment's is its date.
    return { to: /^(cost|rent):/.test(key ?? '') ? '/properties/$id' : '/loans/$id', params: { id } };
  },
};
