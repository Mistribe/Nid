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
import { FEE, invalidateLoans, type LoanDetail, type LoanFee } from '../api';
import { feeKindText, money } from '../format';
import { presentRefusal } from '../refusal';
import { FeeForm } from './FeeForm';

const th = 'px-2 py-1 text-left font-medium';
const td = 'px-2 py-1';
const num = 'px-2 py-1 text-right tabular-nums';

/**
 * 3B2 design §6: the loan's fees by date (the server's order), edited in place as income's one-offs: the section owns
 * its forms (one open at a time), its live region and where the focus lands. A penalty linked to a repayment is changed
 * through it and has no action here.
 */
export function FeesSection({ loan }: { loan: LoanDetail }) {
  const { t } = useTranslation();
  const writable = loan.canWrite;
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [focusId, setFocusId] = useState<string | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addButtonRef = useRef<HTMLButtonElement>(null);

  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const startAdd = () => { setAnnouncement(''); setEditingId(null); setAdding(true); };
  const closeAdd = (message?: string) => { setAdding(false); if (message) announce(message); else addButtonRef.current?.focus(); };
  const startEdit = (id: string) => { setAnnouncement(''); setAdding(false); setEditingId(id); };
  const closeEdit = (message?: string) => { const was = editingId; setEditingId(null); if (message) announce(message); else setFocusId(was); };
  useEffect(() => {
    if (!focusId) return;
    (document.getElementById(`correct-fee-${focusId}`) ?? headingRef.current)?.focus();
    setFocusId(null);
  }, [focusId]);
  // A correction whose row is gone (deleted elsewhere, then « Recharger ») closes with it, onto the heading.
  useEffect(() => {
    if (editingId && !loan.fees.some((f) => f.id === editingId)) { setEditingId(null); headingRef.current?.focus(); }
  }, [editingId, loan.fees]);

  const cols = writable ? 5 : 4;
  return (
    <Section title={t('loan_detail.fees')} headingRef={headingRef} announcement={announcement}>
      {writable ? <Button ref={addButtonRef} type="button" variant="outline" onClick={startAdd}>{t('loan_fees.add')}</Button> : null}
      {writable && adding ? <FeeForm loan={loan} onDone={closeAdd} /> : null}
      <div className="overflow-x-auto">
        <table className="w-full text-sm" aria-label={t('loan_fees.table')}>
          <thead>
            <tr>
              <th scope="col" className={th}>{t('loan_detail.col_date')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_kind')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_amount')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_note')}</th>
              {writable ? <th scope="col" className={th}>{t('loan_detail.col_actions')}</th> : null}
            </tr>
          </thead>
          <tbody>
            {loan.fees.length === 0 ? (
              <tr><td colSpan={cols} className={`${td} text-muted-foreground`}>{t('loan_fees.empty')}</td></tr>
            ) : loan.fees.map((f) => writable && editingId === f.id ? (
              <tr key={f.id}><td colSpan={cols} className={td}><FeeForm loan={loan} editing={f} onDone={closeEdit} /></td></tr>
            ) : (
              <tr key={f.id} className="border-t">
                <td className={td}><LocalDate value={f.onDate} /></td>
                <td className={td}>{feeKindText(t, f.kind)}</td>
                <td className={num}>{money(loan.currency, f.amount)}</td>
                <td className={td}>{f.note}</td>
                {writable ? (
                  <td className={td}>
                    {f.repaymentId ? t('loan_fees.linked') : (
                      <FeeRowActions loan={loan} fee={f} onEdit={() => startEdit(f.id)} onDeleteStarted={() => setAnnouncement('')}
                        onDeleted={announce} onReloaded={() => headingRef.current?.focus()} />
                    )}
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

/** « Corriger » swaps the row for its form; « Supprimer » asks first and sends the version read. */
function FeeRowActions({ loan, fee, onEdit, onDeleteStarted, onDeleted, onReloaded }: {
  loan: LoanDetail; fee: LoanFee; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void; onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', FEE, {
    onSuccess: () => { void invalidateLoans(queryClient); onDeleted(t('loan_fees.deleted')); },
  });
  const problem = remove.error ? presentRefusal(t, remove.error) : null;

  return (
    <span className="flex flex-wrap items-center gap-2">
      <Button id={`correct-fee-${fee.id}`} type="button" variant="outline" size="sm" onClick={onEdit}>{t('loan_detail.correct')}</Button>
      <ConfirmAction label={t('loan_detail.delete')} confirmation={t('loan_fees.delete_confirm')} pending={remove.isPending}
        onConfirm={() => { onDeleteStarted(); remove.mutate({ params: { path: { id: loan.id, feeId: fee.id }, query: { version: fee.version } } }); }} />
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found' ? <Reload onReload={() => { remove.reset(); return invalidateLoans(queryClient).then(onReloaded); }} /> : null}
        </>
      ) : null}
    </span>
  );
}
