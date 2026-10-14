import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ConfirmAction } from '@/components/ConfirmAction';
import { LocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Section } from '../../income/Section';
import { money } from '../../loans/format';
import { presentRefusal } from '../../loans/refusal';
import { Reload } from '../../subscriptions/edits/Reload';
import { FLOW, invalidateProperties, type PropertyDetail, type PropertyFlow } from '../api';
import { categoryText, kindText } from '../format';
import { FlowForm } from './FlowForm';

const th = 'px-2 py-1 text-left font-medium';
const td = 'px-2 py-1';
const num = 'px-2 py-1 text-right tabular-nums';

/**
 * 3D1 design §6: the property's expenses and income, newest first (the server lists them oldest first), edited in place
 * as the loan fees: the section owns its forms (one open at a time), its live region and where the focus lands.
 */
export function FlowsSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const writable = property.canWrite;
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
    (document.getElementById(`correct-flow-${focusId}`) ?? headingRef.current)?.focus();
    setFocusId(null);
  }, [focusId]);
  // A correction whose row is gone (superseded or deleted elsewhere, then « Recharger ») closes with it, onto the heading.
  useEffect(() => {
    if (editingId && !property.flows.some((f) => f.id === editingId)) { setEditingId(null); headingRef.current?.focus(); }
  }, [editingId, property.flows]);

  const newestFirst = [...property.flows].reverse();
  const cols = writable ? 6 : 5;
  return (
    <Section title={t('property_flows.title')} headingRef={headingRef} announcement={announcement}>
      {writable ? <Button ref={addButtonRef} type="button" variant="outline" onClick={startAdd}>{t('property_flows.add')}</Button> : null}
      {writable && adding ? <FlowForm property={property} onDone={closeAdd} /> : null}
      <div className="overflow-x-auto">
        <table className="w-full text-sm" aria-label={t('property_flows.table')}>
          <thead>
            <tr>
              <th scope="col" className={th}>{t('loan_detail.col_date')}</th>
              <th scope="col" className={th}>{t('property_flows.kind')}</th>
              <th scope="col" className={th}>{t('property_flows.category')}</th>
              <th scope="col" className={th}>{t('property_flows.amount')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_note')}</th>
              {writable ? <th scope="col" className={th}>{t('loan_detail.col_actions')}</th> : null}
            </tr>
          </thead>
          <tbody>
            {newestFirst.length === 0 ? (
              <tr><td colSpan={cols} className={`${td} text-muted-foreground`}>{t('property_flows.empty')}</td></tr>
            ) : newestFirst.map((f) => writable && editingId === f.id ? (
              <tr key={f.id}><td colSpan={cols} className={td}><FlowForm property={property} editing={f} onDone={closeEdit} /></td></tr>
            ) : (
              <tr key={f.id} className="border-t">
                <td className={td}><LocalDate value={f.occurredOn} /></td>
                <td className={td}>{kindText(t, f.kind)}</td>
                <td className={td}>{categoryText(t, f.category)}</td>
                <td className={num}>{money(property.currency, f.amount)}</td>
                <td className={td}>{f.note}</td>
                {writable ? (
                  <td className={td}>
                    <FlowRowActions property={property} flow={f} onEdit={() => startEdit(f.id)} onDeleteStarted={() => setAnnouncement('')}
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

/** « Corriger » swaps the row for its form; « Supprimer » asks first. */
function FlowRowActions({ property, flow, onEdit, onDeleteStarted, onDeleted, onReloaded }: {
  property: PropertyDetail; flow: PropertyFlow; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void; onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', FLOW, {
    onSuccess: () => { void invalidateProperties(queryClient); onDeleted(t('property_flows.deleted')); },
  });
  const problem = remove.error ? presentRefusal(t, remove.error) : null;

  return (
    <span className="flex flex-wrap items-center gap-2">
      <Button id={`correct-flow-${flow.id}`} type="button" variant="outline" size="sm" onClick={onEdit}>{t('loan_detail.correct')}</Button>
      <ConfirmAction label={t('loan_detail.delete')} confirmation={t('property_flows.delete_confirm')} pending={remove.isPending}
        onConfirm={() => { onDeleteStarted(); remove.mutate({ params: { path: { id: property.id, flowId: flow.id } } }); }} />
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found'
            ? <Reload onReload={() => { remove.reset(); return invalidateProperties(queryClient).then(onReloaded); }} />
            : null}
        </>
      ) : null}
    </span>
  );
}
