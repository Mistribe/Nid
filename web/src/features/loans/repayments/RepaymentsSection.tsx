import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { RowActions } from '@/components/page/RowActions';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { invalidateLoans, REPAYMENT, type LoanDetail, type LoanRepayment } from '../api';
import { effectText, money } from '../format';
import { presentRefusal } from '../refusal';
import type { RepaymentPrefill } from './repayment';
import { RepaymentDialog } from './RepaymentForm';
import '@/locales/loans';

/**
 * 3B2 design §7: the early repayments by date (the server's order). Adding (possibly from a simulation) and correcting
 * open a dialog; the section owns its live region and where the focus lands; a deletion the engine refuses is said in
 * the row.
 */
export function RepaymentsSection({ loan, prefill = null, onConsumed }: { loan: LoanDetail; prefill?: RepaymentPrefill | null; onConsumed?: () => void }) {
  const { t } = useTranslation();
  const writable = loan.canWrite;
  const [dialog, setDialog] = useState<{ editing?: LoanRepayment; prefill?: RepaymentPrefill | null } | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [focusId, setFocusId] = useState<string | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addButtonRef = useRef<HTMLButtonElement>(null);

  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const open = (editing?: LoanRepayment) => { setAnnouncement(''); setDialog({ editing }); };
  const saved = (message: string) => { setDialog(null); announce(message); };
  const cancel = () => { const row = dialog?.editing?.id; setDialog(null); if (row) setFocusId(row); else addButtonRef.current?.focus(); };
  // « Enregistrer ce remboursement » (3B2 design §8): each press opens the add dialog afresh with its column.
  useEffect(() => {
    if (!prefill) return;
    setAnnouncement('');
    setDialog({ prefill });
    // Consumed once: this tab unmounts and remounts, and must not reopen the dialog with old values.
    onConsumed?.();
  }, [prefill]); // eslint-disable-line react-hooks/exhaustive-deps -- only a new prefill opens the dialog
  useEffect(() => {
    if (!focusId) return;
    (document.getElementById(`correct-repayment-${focusId}`) ?? headingRef.current)?.focus();
    setFocusId(null);
  }, [focusId]);
  // A correction whose row is gone (deleted elsewhere, then « Recharger ») closes with it, onto the heading.
  useEffect(() => {
    if (dialog?.editing && !loan.repayments.some((r) => r.id === dialog.editing?.id)) { setDialog(null); headingRef.current?.focus(); }
  }, [dialog, loan.repayments]);

  const m = (amount: string) => money(loan.currency, amount);
  return (
    <Section title={t('loan_detail.repayments')} headingRef={headingRef} announcement={announcement}>
      {writable ? <Button ref={addButtonRef} type="button" variant="outline" size="sm" onClick={() => open()}>{t('loan_repayments.add')}</Button> : null}
      {writable && dialog ? <RepaymentDialog key={dialog.prefill?.seq ?? 0} loan={loan} editing={dialog.editing} prefill={dialog.prefill} onClose={cancel} onDone={saved} /> : null}
      <Table aria-label={t('loan_repayments.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('loan_detail.col_date')}</TableHead>
            <TableHead className="text-right">{t('loan_detail.col_amount')}</TableHead>
            <TableHead>{t('loan_detail.col_effect')}</TableHead>
            <TableHead className="text-right">{t('loan_detail.col_penalty')}</TableHead>
            <TableHead>{t('loan_detail.col_note')}</TableHead>
            {writable ? <TableHead className="w-0"><span className="sr-only">{t('loan_detail.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {loan.repayments.length === 0 ? (
            <TableRow><TableCell colSpan={writable ? 6 : 5} className="text-muted-foreground">{t('loan_repayments.empty')}</TableCell></TableRow>
          ) : loan.repayments.map((r) => (
            <TableRow key={r.id}>
              <TableCell><LocalDate value={r.onDate} /></TableCell>
              <TableCell className="text-right tabular-nums">{m(r.amount)}</TableCell>
              <TableCell>{effectText(t, r.effect)}</TableCell>
              <TableCell className="text-right tabular-nums">{r.penalty ? m(r.penalty) : null}</TableCell>
              <TableCell className="whitespace-normal">{r.note}</TableCell>
              {writable ? (
                <TableCell>
                  <RepaymentRowActions loan={loan} repayment={r} onEdit={() => open(r)} onDeleteStarted={() => setAnnouncement('')}
                    onDeleted={announce} onReloaded={() => headingRef.current?.focus()} />
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}

/** « Corriger » opens the dialog; « Supprimer » asks first, naming the penalty that goes with it. */
function RepaymentRowActions({ loan, repayment, onEdit, onDeleteStarted, onDeleted, onReloaded }: {
  loan: LoanDetail; repayment: LoanRepayment; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void;
  onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', REPAYMENT, {
    onSuccess: () => { void invalidateLoans(queryClient); onDeleted(t('loan_repayments.deleted')); },
  });
  const confirmation = repayment.penalty
    ? t('loan_repayments.delete_confirm_penalty', { amount: money(loan.currency, repayment.penalty) })
    : t('loan_repayments.delete_confirm');

  return (
    <RowActions menuId={`correct-repayment-${repayment.id}`} menuLabel={t('loan_detail.row_menu_what', { date: formatLocalDate(repayment.onDate), what: money(loan.currency, repayment.amount) })}
      items={[{ label: t('loan_detail.correct'), onSelect: onEdit }]} confirmation={confirmation}
      remove={remove} onDeleteStarted={onDeleteStarted} onReloaded={onReloaded} present={presentRefusal} invalidate={invalidateLoans}
      onConfirm={() => remove.mutate({ params: { path: { id: loan.id, eventId: repayment.id }, query: { version: repayment.version } } })} />
  );
}
