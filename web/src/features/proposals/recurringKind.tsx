import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Row, notEditable, type KindUi, type Money } from './kindParts';
import '@/locales/proposals';

type RecurringPayload = { subscription: { displayName: string; phases?: { price?: Money }[] } };
type RecurringPreview = { account?: string; cadence?: 'monthly' | 'quarterly' | 'yearly' | null; lines?: { bookedOn: string; amount: string; currency: string }[] };

const asRecurring = (payload: unknown) => (payload as RecurringPayload).subscription;

/** 5C §6.4: the subscription proposed (from the payload: the route's own preview is empty), its rhythm, the paying account and the lines seen. */
function RecurringPreviewView({ preview, original }: { preview: unknown; original: unknown }) {
  const { t } = useTranslation();
  const p = preview as RecurringPreview;
  const s = asRecurring(original);
  const price = s.phases?.[0]?.price;
  const none = t('proposal.preview.none');
  return (
    <div className="space-y-3">
      <Table>
        <caption className="sr-only">{t('proposal.preview.title')}</caption>
        <TableBody>
          <Row label={t('proposal.recurring.name')}>{s.displayName}</Row>
          <Row label={t('proposal.recurring.price')}>{price ? formatMoney(price) : none}</Row>
          <Row label={t('proposal.recurring.cadence')}>{p.cadence ? t(`proposal.recurring.every.${p.cadence}`) : none}</Row>
          <Row label={t('proposal.recurring.account')}>{p.account ?? none}</Row>
        </TableBody>
      </Table>
      {p.lines?.length ? (
        <Table>
          <caption className="text-left font-medium">{t('proposal.recurring.lines')}</caption>
          <TableHeader>
            <TableRow><TableHead>{t('proposal.recurring.date')}</TableHead><TableHead className="text-right">{t('proposal.recurring.amount')}</TableHead></TableRow>
          </TableHeader>
          <TableBody>
            {p.lines.map((l, i) => (
              <TableRow key={i}><TableCell>{formatLocalDate(l.bookedOn)}</TableCell><TableCell className="text-right tabular-nums">{formatMoney(l)}</TableCell></TableRow>
            ))}
          </TableBody>
        </Table>
      ) : null}
    </div>
  );
}

export const recurring: KindUi = {
  label: 'proposals.kind.recurring',
  summary: (t, p) => {
    const s = asRecurring(p.editedPayload ?? p.payload);
    const price = s.phases?.[0]?.price;
    return t('proposals.summary.recurring', { name: s.displayName, amount: price ? formatMoney(price) : t('proposal.preview.none') });
  },
  Preview: RecurringPreviewView,
  ...notEditable((t) => t('proposal.recurring.as_proposed')),
  // Approved, the route answered the subscription it created.
  link: (p) => ({ to: '/subscriptions/$id', params: { id: p.appliedRefs[0] ?? '' } }),
};
