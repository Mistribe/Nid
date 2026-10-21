import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { RowActions } from '@/components/page/RowActions';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { FEE, invalidateLoans, type LoanDetail, type LoanFee } from '../api';
import { feeKindText, money } from '../format';
import { presentRefusal } from '../refusal';
import { FeeDialog } from './FeeForm';
import '@/locales/loans';

/**
 * 3B2 design §6: the loan's fees by date (the server's order). Adding and correcting open a dialog; the section owns
 * its live region and where the focus lands (a saved correction replaces its row). A penalty linked to a repayment is
 * changed through it and has no action here.
 */
export function FeesSection({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const writable = loan.canWrite;
  const [dialog, setDialog] = useState<{ editing?: LoanFee } | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [focusId, setFocusId] = useState<string | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addButtonRef = useRef<HTMLButtonElement>(null);

  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const open = (editing?: LoanFee) => { setAnnouncement(''); setDialog({ editing }); };
  // A saved fee announces (its row, if corrected, is a new one); « Annuler » returns to what opened the dialog.
  const saved = (message: string) => { setDialog(null); announce(message); };
  const cancel = () => { const row = dialog?.editing?.id; setDialog(null); if (row) setFocusId(row); else addButtonRef.current?.focus(); };
  useEffect(() => {
    if (!focusId) return;
    (document.getElementById(`correct-fee-${focusId}`) ?? headingRef.current)?.focus();
    setFocusId(null);
  }, [focusId]);
  // A correction whose row is gone (deleted elsewhere, then « Recharger ») closes with it, onto the heading.
  useEffect(() => {
    if (dialog?.editing && !loan.fees.some((f) => f.id === dialog.editing?.id)) { setDialog(null); headingRef.current?.focus(); }
  }, [dialog, loan.fees]);

  return (
    <Section title={t('loan_detail.fees')} headingRef={headingRef} announcement={announcement}>
      {writable ? <Button ref={addButtonRef} type="button" variant="outline" size="sm" onClick={() => open()}>{t('loan_fees.add')}</Button> : null}
      {writable && dialog ? <FeeDialog loan={loan} editing={dialog.editing} onClose={cancel} onDone={saved} /> : null}
      <Table aria-label={t('loan_fees.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('loan_detail.col_date')}</TableHead>
            <TableHead>{t('loan_detail.col_kind')}</TableHead>
            <TableHead className="text-right">{t('loan_detail.col_amount')}</TableHead>
            <TableHead>{t('loan_detail.col_note')}</TableHead>
            {writable ? <TableHead className="w-0"><span className="sr-only">{t('loan_detail.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {loan.fees.length === 0 ? (
            <TableRow><TableCell colSpan={writable ? 5 : 4} className="text-muted-foreground">{t('loan_fees.empty')}</TableCell></TableRow>
          ) : loan.fees.map((f) => (
            <TableRow key={f.id}>
              <TableCell><LocalDate value={f.onDate} /></TableCell>
              <TableCell>{feeKindText(t, f.kind)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(loan.currency, f.amount)}</TableCell>
              <TableCell className="whitespace-normal">{f.note}</TableCell>
              {writable ? (
                <TableCell>
                  {f.repaymentId ? t('loan_fees.linked') : (
                    <FeeRowActions loan={loan} fee={f} onEdit={() => open(f)} onDeleteStarted={() => setAnnouncement('')}
                      onDeleted={announce} onReloaded={() => headingRef.current?.focus()} />
                  )}
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}

/** « Corriger » opens the dialog; « Supprimer » asks first and sends the version read. */
function FeeRowActions({ loan, fee, onEdit, onDeleteStarted, onDeleted, onReloaded }: {
  loan: LoanDetail; fee: LoanFee; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void; onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', FEE, {
    onSuccess: () => { void invalidateLoans(queryClient); onDeleted(t('loan_fees.deleted')); },
  });

  return (
    <RowActions menuId={`correct-fee-${fee.id}`} menuLabel={t('loan_detail.row_menu_what', { date: formatLocalDate(fee.onDate), what: feeKindText(t, fee.kind) })}
      items={[{ label: t('loan_detail.correct'), onSelect: onEdit }]} confirmation={t('loan_fees.delete_confirm')}
      remove={remove} onDeleteStarted={onDeleteStarted} onReloaded={onReloaded} present={presentRefusal} invalidate={invalidateLoans}
      onConfirm={() => remove.mutate({ params: { path: { id: loan.id, feeId: fee.id }, query: { version: fee.version } } })} />
  );
}
