import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { Button } from '@/components/ui/button';
import type { IncomeSourceDetail } from '../api';
import { Section } from '../Section';
import { OneOffForm } from './OneOffForm';
import { OneOffRowActions } from './OneOffRowActions';

/**
 * Spec §4: the last twelve months' one-off income, newest first. The section owns its forms (one open at a time), its
 * live region and where the focus lands; without write access it is a read-only table.
 */
export function OneOffsSection({ source, writable }: { source: IncomeSourceDetail; writable: boolean }) {
  const { t } = useTranslation();
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
    document.getElementById(`correct-one-off-${focusId}`)?.focus();
    setFocusId(null);
  }, [focusId]);
  // A source that turns ended (or read-only) drops its open form, so it does not come back on reopening.
  useEffect(() => {
    if (!writable) { setAdding(false); setEditingId(null); }
  }, [writable]);

  const rows = [...source.oneOffs].sort((a, b) => (a.occurredOn < b.occurredOn ? 1 : a.occurredOn > b.occurredOn ? -1 : 0));
  const cols = writable ? 4 : 3;

  return (
    <Section title={t('income_detail.one_offs.title')} headingRef={headingRef} announcement={announcement}>
      {writable ? <Button ref={addButtonRef} type="button" variant="outline" onClick={startAdd}>{t('income_detail.one_offs.add')}</Button> : null}
      {writable && adding ? <OneOffForm source={source} onDone={closeAdd} /> : null}
      <table className="w-full text-sm" aria-label={t('income_detail.one_offs.table')}>
        <thead>
          <tr>
            <th scope="col" className="text-left">{t('income_detail.one_offs.date')}</th>
            <th scope="col" className="text-left">{t('income_detail.one_offs.amount')}</th>
            <th scope="col" className="text-left">{t('income_detail.one_offs.note')}</th>
            {writable ? <th scope="col" className="text-left">{t('income_detail.col_actions')}</th> : null}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr><td colSpan={cols} className="text-muted-foreground">{t('income_detail.one_offs.empty')}</td></tr>
          ) : rows.map((f) => writable && editingId === f.id ? (
            <tr key={f.id}><td colSpan={cols}><OneOffForm source={source} editing={f} onDone={closeEdit} /></td></tr>
          ) : (
            <tr key={f.id}>
              <td><LocalDate value={f.occurredOn} /></td>
              <td><Money value={f.amount} tabular /></td>
              <td>{f.note}</td>
              {writable ? (
                <td><OneOffRowActions source={source} oneOff={f} onEdit={() => startEdit(f.id)} onDeleteStarted={() => setAnnouncement('')} onDeleted={announce} /></td>
              ) : null}
            </tr>
          ))}
        </tbody>
      </table>
    </Section>
  );
}
