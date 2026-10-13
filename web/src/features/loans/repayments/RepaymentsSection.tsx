import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ConfirmAction } from '@/components/ConfirmAction';
import { LocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Section } from '../../income/Section';
import { Reload } from '../../subscriptions/edits/Reload';
import { invalidateLoans, REPAYMENT, type LoanDetail, type LoanRepayment } from '../api';
import { effectText, money } from '../format';
import { presentRefusal } from '../refusal';
import type { RepaymentPrefill } from './repayment';
import { RepaymentForm } from './RepaymentForm';

const th = 'px-2 py-1 text-left font-medium';
const td = 'px-2 py-1';
const num = 'px-2 py-1 text-right tabular-nums';

/**
 * 3B2 design §7: the early repayments by date (the server's order), edited in place as the fees: one form open at a
 * time, the section's live region and focus; a deletion the engine refuses is said in the row.
 */
export function RepaymentsSection({ loan, prefill = null }: { loan: LoanDetail; prefill?: RepaymentPrefill | null }) {
  const { t } = useTranslation();
  const writable = loan.canWrite;
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [focusId, setFocusId] = useState<string | null>(null);
  const [prefilled, setPrefilled] = useState<RepaymentPrefill | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addButtonRef = useRef<HTMLButtonElement>(null);

  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const startAdd = () => { setAnnouncement(''); setEditingId(null); setPrefilled(null); setAdding(true); };
  const closeAdd = (message?: string) => { setAdding(false); setPrefilled(null); if (message) announce(message); else addButtonRef.current?.focus(); };
  // « Enregistrer ce remboursement » (3B2 design §8): each press opens the add form afresh with its column; the form takes the focus.
  useEffect(() => {
    if (!prefill) return;
    setAnnouncement('');
    setEditingId(null);
    setPrefilled(prefill);
    setAdding(true);
  }, [prefill]);
  const startEdit = (id: string) => { setAnnouncement(''); setAdding(false); setEditingId(id); };
  const closeEdit = (message?: string) => { const was = editingId; setEditingId(null); if (message) announce(message); else setFocusId(was); };
  useEffect(() => {
    if (!focusId) return;
    (document.getElementById(`correct-repayment-${focusId}`) ?? headingRef.current)?.focus();
    setFocusId(null);
  }, [focusId]);
  // A correction whose row is gone (deleted elsewhere, then « Recharger ») closes with it, onto the heading.
  useEffect(() => {
    if (editingId && !loan.repayments.some((r) => r.id === editingId)) { setEditingId(null); headingRef.current?.focus(); }
  }, [editingId, loan.repayments]);

  const cols = writable ? 6 : 5;
  const m = (amount: string) => money(loan.currency, amount);
  return (
    <Section title={t('loan_detail.repayments')} headingRef={headingRef} announcement={announcement}>
      {writable ? <Button ref={addButtonRef} type="button" variant="outline" onClick={startAdd}>{t('loan_repayments.add')}</Button> : null}
      {writable && adding ? <RepaymentForm key={prefilled?.seq ?? 0} loan={loan} prefill={prefilled} onDone={closeAdd} /> : null}
      <div className="overflow-x-auto">
        <table className="w-full text-sm" aria-label={t('loan_repayments.table')}>
          <thead>
            <tr>
              <th scope="col" className={th}>{t('loan_detail.col_date')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_amount')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_effect')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_penalty')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_note')}</th>
              {writable ? <th scope="col" className={th}>{t('loan_detail.col_actions')}</th> : null}
            </tr>
          </thead>
          <tbody>
            {loan.repayments.length === 0 ? (
              <tr><td colSpan={cols} className={`${td} text-muted-foreground`}>{t('loan_repayments.empty')}</td></tr>
            ) : loan.repayments.map((r) => writable && editingId === r.id ? (
              <tr key={r.id}><td colSpan={cols} className={td}><RepaymentForm loan={loan} editing={r} onDone={closeEdit} /></td></tr>
            ) : (
              <tr key={r.id} className="border-t">
                <td className={td}><LocalDate value={r.onDate} /></td>
                <td className={num}>{m(r.amount)}</td>
                <td className={td}>{effectText(t, r.effect)}</td>
                <td className={num}>{r.penalty ? m(r.penalty) : null}</td>
                <td className={td}>{r.note}</td>
                {writable ? (
                  <td className={td}>
                    <RepaymentRowActions loan={loan} repayment={r} onEdit={() => startEdit(r.id)} onDeleteStarted={() => setAnnouncement('')}
                      onDeleted={announce} onReloaded={() => headingRef.current?.focus()} />
                  </td>
                ) : null}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Section>
  );
}

/** « Corriger » swaps the row for its form; « Supprimer » asks first, naming the penalty that goes with it. */
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
  const problem = remove.error ? presentRefusal(t, remove.error) : null;
  const confirmation = repayment.penalty
    ? t('loan_repayments.delete_confirm_penalty', { amount: money(loan.currency, repayment.penalty) })
    : t('loan_repayments.delete_confirm');

  return (
    <span className="flex flex-wrap items-center gap-2">
      <Button id={`correct-repayment-${repayment.id}`} type="button" variant="outline" size="sm" onClick={onEdit}>{t('loan_detail.correct')}</Button>
      <ConfirmAction label={t('loan_detail.delete')} confirmation={confirmation} pending={remove.isPending}
        onConfirm={() => { onDeleteStarted(); remove.mutate({ params: { path: { id: loan.id, eventId: repayment.id }, query: { version: repayment.version } } }); }} />
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => { remove.reset(); return invalidateLoans(queryClient).then(onReloaded); }} /> : null}
        </>
      ) : null}
    </span>
  );
}
