import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import type { EnvelopeDetail } from '../api';
import { useTransactions, type BankTransaction } from './api';
import ImportStatementDialog from './ImportStatementDialog';
import '@/locales/banks';

const ISO = /^\d{4}-\d{2}-\d{2}$/;

/** Bank sync spec §6: the booked lines of the envelope's bank account and its cards, newest first, page by page, and « Importer un relevé ». */
export default function OperationsTab({ envelope, writable }: { envelope: EnvelopeDetail; writable: boolean }) {
  const { t } = useTranslation();
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [importing, setImporting] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const [landOn, setLandOn] = useState(false);
  const importRef = useRef<HTMLButtonElement>(null);
  // A half-typed date asks nothing: only a whole one bounds the period.
  const filtered = ISO.test(from) || ISO.test(to);
  const lines = useTransactions(envelope.id, ISO.test(from) ? from : undefined, ISO.test(to) ? to : undefined);
  const rows = lines.data?.pages.flatMap((p) => p.items) ?? [];
  // Once the dialog handed itself back, the focus returns to its button, never <body>.
  useEffect(() => { if (landOn) { importRef.current?.focus(); setLandOn(false); } }, [landOn]);

  const label = (l: BankTransaction) => (
    <span className="whitespace-normal">
      <span className="flex flex-wrap items-center gap-2">{l.label}{l.fromCard ? <Badge variant="secondary">{t('banks.operations.card')}</Badge> : null}</span>
      {l.counterparty || l.cardName ? (
        <span className="block text-sm text-muted-foreground">{[l.counterparty, l.cardName].filter(Boolean).join(' · ')}</span>
      ) : null}
    </span>
  );
  const amount = (l: BankTransaction) => <span className="tabular-nums">{formatCurrency(l.amount, l.currency)}</span>;

  return (
    <Section title={t('banks.operations.title')} srTitle announcement={announcement}>
      <div className="flex flex-wrap items-end gap-3">
        <div className="w-44"><Field label={t('banks.operations.from')}>{(c) => <DateField {...c} value={from} onChange={setFrom} />}</Field></div>
        <div className="w-44"><Field label={t('banks.operations.to')}>{(c) => <DateField {...c} value={to} onChange={setTo} />}</Field></div>
        {writable ? (
          <Button ref={importRef} type="button" variant="outline" className="ml-auto" onClick={() => { setAnnouncement(''); setImporting(true); }}>
            {t('banks.import.open')}
          </Button>
        ) : null}
      </div>
      {lines.error ? <ProblemMessage problem={present(toProblem(lines.error))} /> : null}
      {lines.isPending ? <div role="status" aria-busy="true" aria-label={t('banks.operations.loading')} className="h-40 shimmer rounded-md border bg-muted" /> : null}
      {lines.data && rows.length === 0 ? <p className="text-muted-foreground">{t(filtered ? 'banks.operations.empty_period' : 'banks.operations.empty')}</p> : null}
      {rows.length > 0 ? (
        <SortableTable<BankTransaction> caption={t('banks.operations.title')} rows={rows} rowKey={(l) => l.id}
          columns={[
            { id: 'date', header: t('banks.operations.date'), sort: (l) => l.bookedOn, cell: (l) => <LocalDate value={l.bookedOn} style="short" /> },
            { id: 'label', header: t('banks.operations.label'), cell: label },
            { id: 'amount', header: t('banks.operations.amount'), align: 'end', sort: (l) => amountKey(l.amount), cell: amount },
          ]}
          mobile={(l) => (
            <div className="space-y-1">
              <div className="flex items-start justify-between gap-2">{label(l)}{amount(l)}</div>
              <div className="text-sm text-muted-foreground"><LocalDate value={l.bookedOn} style="short" /></div>
            </div>
          )} />
      ) : null}
      {lines.hasNextPage ? (
        <Button type="button" variant="outline" disabled={lines.isFetchingNextPage} onClick={() => void lines.fetchNextPage()}>{t('common.more')}</Button>
      ) : null}
      {importing ? (
        <ImportStatementDialog envelopeId={envelope.id} onClose={() => setImporting(false)}
          onDone={(message) => { setImporting(false); setAnnouncement(message); setLandOn(true); }} />
      ) : null}
    </Section>
  );
}
