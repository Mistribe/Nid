import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { CircleDot } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ConfirmAction } from '@/components/ConfirmAction';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Section } from '../../income/Section';
import { money } from '../../loans/format';
import { presentRefusal } from '../../loans/refusal';
import { Reload } from '../../subscriptions/edits/Reload';
import { invalidateProperties, TENANCY, type PropertyDetail, type PropertyTenancy } from '../api';
import { RentalFigures } from './RentalFigures';
import { TenancyForm } from './TenancyForm';

const th = 'px-2 py-1 text-left font-medium';
const td = 'px-2 py-1';
const num = 'px-2 py-1 text-right tabular-nums';

/** « depuis le … », or « du … au … » once it has an end. */
const periodText = (t: TFunction, x: PropertyTenancy) => (x.endsOn
  ? t('property_rental.between', { start: formatLocalDate(x.startsOn), end: formatLocalDate(x.endsOn) })
  : t('property_rental.since', { start: formatLocalDate(x.startsOn) }));

/**
 * Spec 3D2 §3: « Location » — the rental figures, then the tenancies newest first (the server lists them by start), edited
 * in place as the loan fees: the section owns its forms (one open at a time), its live region and where the focus lands.
 */
export function RentalSection({ property }: { property: PropertyDetail }) {
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
    (document.getElementById(`correct-tenancy-${focusId}`) ?? headingRef.current)?.focus();
    setFocusId(null);
  }, [focusId]);
  // A correction whose row is gone (deleted elsewhere, then « Recharger ») closes with it, onto the heading.
  useEffect(() => {
    if (editingId && !property.tenancies.some((x) => x.id === editingId)) { setEditingId(null); headingRef.current?.focus(); }
  }, [editingId, property.tenancies]);

  const newestFirst = [...property.tenancies].reverse();
  const cols = writable ? 7 : 6;
  return (
    <Section title={t('property_rental.title')} headingRef={headingRef} announcement={announcement}>
      <RentalFigures property={property} />
      {writable ? <Button ref={addButtonRef} type="button" variant="outline" onClick={startAdd}>{t('property_rental.add')}</Button> : null}
      {writable && adding ? <TenancyForm property={property} onDone={closeAdd} /> : null}
      {newestFirst.length === 0 ? null : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm" aria-label={t('property_rental.table')}>
            <thead>
              <tr>
                <th scope="col" className={th}>{t('property_rental.label')}</th>
                <th scope="col" className={th}>{t('property_rental.period')}</th>
                <th scope="col" className={th}>{t('property_rental.rent')}</th>
                <th scope="col" className={th}>{t('property_rental.charges')}</th>
                <th scope="col" className={th}>{t('property_rental.deposit')}</th>
                <th scope="col" className={th}>{t('loan_detail.col_note')}</th>
                {writable ? <th scope="col" className={th}>{t('loan_detail.col_actions')}</th> : null}
              </tr>
            </thead>
            <tbody>
              {newestFirst.map((x) => writable && editingId === x.id ? (
                <tr key={x.id}><td colSpan={cols} className={td}><TenancyForm property={property} editing={x} onDone={closeEdit} /></td></tr>
              ) : (
                <tr key={x.id} className="border-t">
                  <td className={td}>
                    <span className="flex flex-wrap items-center gap-2">
                      {x.label}
                      {/* Colour never alone: an icon and the word, on the tenancy the server says covers today. */}
                      {x.current ? (
                        <span className="inline-flex items-center gap-1">
                          <CircleDot aria-hidden="true" className="size-4" />{t('property_rental.current')}
                        </span>
                      ) : null}
                    </span>
                  </td>
                  <td className={td}>{periodText(t, x)}</td>
                  <td className={num}>{money(property.currency, x.rentMonthly)}</td>
                  <td className={num}>{money(property.currency, x.chargesMonthly)}</td>
                  <td className={num}>{x.deposit ? money(property.currency, x.deposit) : null}</td>
                  <td className={td}>{x.note}</td>
                  {writable ? (
                    <td className={td}>
                      <TenancyRowActions property={property} tenancy={x} onEdit={() => startEdit(x.id)} onDeleteStarted={() => setAnnouncement('')}
                        onDeleted={announce} onReloaded={() => headingRef.current?.focus()} />
                    </td>
                  ) : null}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Section>
  );
}

/** « Corriger » swaps the row for its form; « Supprimer » asks first and sends the version read. */
function TenancyRowActions({ property, tenancy, onEdit, onDeleteStarted, onDeleted, onReloaded }: {
  property: PropertyDetail; tenancy: PropertyTenancy; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void;
  onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', TENANCY, {
    onSuccess: () => { void invalidateProperties(queryClient); onDeleted(t('property_rental.deleted')); },
  });
  const problem = remove.error ? presentRefusal(t, remove.error) : null;

  return (
    <span className="flex flex-wrap items-center gap-2">
      <Button id={`correct-tenancy-${tenancy.id}`} type="button" variant="outline" size="sm" onClick={onEdit}>{t('loan_detail.correct')}</Button>
      <ConfirmAction label={t('loan_detail.delete')} confirmation={t('property_rental.delete_confirm')} pending={remove.isPending}
        onConfirm={() => {
          onDeleteStarted();
          remove.mutate({ params: { path: { id: property.id, tenancyId: tenancy.id }, query: { version: tenancy.version } } });
        }} />
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
