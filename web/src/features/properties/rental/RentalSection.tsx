import type { TFunction } from 'i18next';
import { CircleDot } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { ConfirmAction } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '@/components/page/Section';
import { money } from '../../loans/format';
import { invalidateProperties, TENANCY, type PropertyDetail, type PropertyTenancy } from '../api';
import { RowActions } from '@/components/page/RowActions';
import { RowMenu } from '@/components/page/RowMenu';
import { presentRefusal } from '../../loans/refusal';
import { useRowDialog } from '../useRowDialog';
import { ArrearsSection } from '../arrears/ArrearsSection';
import { RentalFigures } from './RentalFigures';
import { REVISION, RevisionDialog } from './RevisionDialog';
import { TenancyDialog } from './TenancyForm';
import '@/locales/loans';
import '@/locales/properties';

/** « depuis le … », or « du … au … » once it has an end; a revised lease runs from its first link's start. */
const periodText = (t: TFunction, x: PropertyTenancy, startsOn = x.startsOn) => (x.endsOn
  ? t('property_rental.between', { start: formatLocalDate(startsOn), end: formatLocalDate(x.endsOn) })
  : t('property_rental.since', { start: formatLocalDate(startsOn) }));

/** « 2026-Q2 » as « 2ᵉ trimestre 2026 ». */
const quarterWords = (t: TFunction, quarter: string) => {
  const [year, q] = quarter.split('-Q');
  return t('property_rental.quarter', { count: Number(q), ordinal: true, year });
};

/**
 * Spec 3D2 §3: « Location » — the rental figures, then the tenancies newest first (the server lists them by start); adding
 * and correcting open a dialog, the section owns its live region and where the focus lands.
 */
export function RentalSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const writable = property.canWrite;
  const s = useRowDialog(property.tenancies, 'tenancy-menu');
  // A revised lease is a chain of tenancies (each `revisedFrom` the one before): one row for its last link, its rents in the history.
  const [revising, setRevising] = useState<PropertyTenancy | null>(null);
  const revised = new Set(property.tenancies.map((x) => x.revisedFrom));
  const newestFirst = [...property.tenancies].reverse().filter((x) => !revised.has(x.id));
  const historyOf = (x: PropertyTenancy): PropertyTenancy[] => {
    const before = property.tenancies.find((y) => y.id === x.revisedFrom);
    return before ? [x, ...historyOf(before)] : [x];
  };
  return (
    <Section title={t('property_rental.title')} headingRef={s.headingRef} announcement={s.announcement}>
      <RentalFigures property={property} />
      {writable ? <Button ref={s.addButtonRef} type="button" variant="outline" size="sm" onClick={() => s.open()}>{t('property_rental.add')}</Button> : null}
      {writable && s.dialog ? <TenancyDialog property={property} editing={s.dialog.editing} onClose={s.cancel} onDone={s.saved} /> : null}
      {writable && revising ? (
        <RevisionDialog property={property} tenancy={revising} onClose={() => setRevising(null)}
          onDone={(message) => { setRevising(null); s.announce(message); }} />
      ) : null}
      {property.figures.rentedSince ? <ArrearsSection propertyId={property.id} /> : null}
      {newestFirst.length === 0 ? null : (
        <Table aria-label={t('property_rental.table')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('property_rental.label')}</TableHead>
              <TableHead>{t('property_rental.period')}</TableHead>
              <TableHead className="text-right">{t('property_rental.rent')}</TableHead>
              <TableHead className="text-right">{t('property_rental.charges')}</TableHead>
              <TableHead className="text-right">{t('property_rental.deposit')}</TableHead>
              <TableHead>{t('loan_detail.col_note')}</TableHead>
              {writable ? <TableHead className="w-0"><span className="sr-only">{t('loan_detail.col_actions')}</span></TableHead> : null}
            </TableRow>
          </TableHeader>
          <TableBody>
            {newestFirst.map((x) => (
              <TableRow key={x.id}>
                <TableCell>
                  <span className="flex flex-wrap items-center gap-2">
                    {x.label}
                    {/* Colour never alone: an icon and the word, on the tenancy the server says covers today. */}
                    {x.current ? (
                      <span className="inline-flex items-center gap-1"><CircleDot aria-hidden="true" className="size-4" />{t('property_rental.current')}</span>
                    ) : null}
                  </span>
                  <RevisionNotes property={property} tenancy={x} history={historyOf(x)} onRevise={() => setRevising(x)} onUndone={s.announce} />
                </TableCell>
                <TableCell>{periodText(t, x, historyOf(x).at(-1)!.startsOn)}</TableCell>
                <TableCell className="text-right tabular-nums">{money(property.currency, x.rentMonthly)}</TableCell>
                <TableCell className="text-right tabular-nums">{money(property.currency, x.chargesMonthly)}</TableCell>
                <TableCell className="text-right tabular-nums">{x.deposit ? money(property.currency, x.deposit) : null}</TableCell>
                <TableCell className="whitespace-normal">{x.note}</TableCell>
                {writable ? (
                  <TableCell>
                    <TenancyRowActions property={property} tenancy={x} onEdit={() => s.open(x)} onDeleteStarted={() => s.setAnnouncement('')}
                      onDeleted={s.announce} onReloaded={() => s.headingRef.current?.focus()} />
                  </TableCell>
                ) : null}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </Section>
  );
}

/** « Corriger » opens the dialog; « Supprimer » asks first and sends the version read — not on a revised link, which is undone instead. */
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
  const menuLabel = t('loan_detail.row_menu_what', { date: formatLocalDate(tenancy.startsOn), what: tenancy.label });
  const items = [{ label: t('loan_detail.correct'), onSelect: onEdit }];
  if (tenancy.revisedFrom) return <RowMenu id={`tenancy-menu-${tenancy.id}`} label={menuLabel} items={items} />;
  return (
    <RowActions menuId={`tenancy-menu-${tenancy.id}`} menuLabel={menuLabel}
      items={items} confirmation={t('property_rental.delete_confirm')} remove={remove} onDeleteStarted={onDeleteStarted} onReloaded={onReloaded}
      present={presentRefusal} invalidate={invalidateProperties}
      onConfirm={() => remove.mutate({ params: { path: { id: property.id, tenancyId: tenancy.id }, query: { version: tenancy.version } } })} />
  );
}

/**
 * What the chain says of its last link: its rent history, the revision the server offers (or why it does not), and « Annuler
 * la révision » when the link is itself a revision.
 */
function RevisionNotes({ property, tenancy, history, onRevise, onUndone }: {
  property: PropertyDetail; tenancy: PropertyTenancy; history: PropertyTenancy[]; onRevise: () => void; onUndone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const undo = $api.useMutation('delete', REVISION, {
    onSuccess: () => { void invalidateProperties(queryClient); onUndone(t('property_rental.revision_undone')); },
  });
  const revision = tenancy.revision;
  const writable = property.canWrite;
  return (
    <div className="space-y-1 text-sm">
      {revision?.state === 'due' ? <p>{t('property_rental.revision_due')}</p> : null}
      {revision?.state === 'irl_missing' && revision.missingQuarter
        ? <p>{t('property_rental.revision_irl_missing', { quarter: quarterWords(t, revision.missingQuarter) })}</p> : null}
      {revision?.state === 'forbidden_dpe' ? <p>{t('property_rental.revision_forbidden')}</p> : null}
      {revision?.lostAnniversary ? <p>{t('property_rental.revision_lost', { date: formatLocalDate(revision.lostAnniversary) })}</p> : null}
      {undo.error ? <ProblemMessage problem={presentRefusal(t, undo.error)} /> : null}
      <div className="flex flex-wrap items-center gap-2">
        {writable && revision?.state === 'due' ? <Button type="button" size="sm" onClick={onRevise}>{t('property_rental.revise')}</Button> : null}
        {writable && tenancy.revisedFrom ? (
          <ConfirmAction label={t('property_rental.undo_revision')} confirmation={t('property_rental.undo_confirm')} pending={undo.isPending}
            onConfirm={() => undo.mutate({ params: { path: { id: property.id, tenancyId: tenancy.id } } })} />
        ) : null}
        {history.length > 1 ? (
          <Button type="button" variant="link" size="sm" aria-expanded={open} className="h-auto p-0" onClick={() => setOpen((v) => !v)}>
            {t('property_rental.history')}
          </Button>
        ) : null}
      </div>
      {open ? (
        <ul aria-label={t('property_rental.history')} className="space-y-0.5 tabular-nums">
          {history.map((h) => (
            <li key={h.id}>{t('property_rental.history_row', { start: formatLocalDate(h.startsOn) })} · {money(property.currency, h.rentMonthly)}</li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
