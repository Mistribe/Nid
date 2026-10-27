import { useNavigate, useSearch } from '@tanstack/react-router';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { IncomeDetailSearch, RootSearch } from '@/app/router';
import { FormDialog } from '@/components/FormDialog';
import { Segmented } from '@/components/form/Segmented';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useRowDialog } from '../../properties/useRowDialog';
import { useAnchoredDocuments, type DocumentSummary } from '../../documents/api';
import { OneOffDocument } from '../payslips/OneOffDocument';
import { PayslipProblem } from '../payslips/PayslipCell';
import { usePayslipWrites } from '../payslips/usePayslipWrites';
import { SOURCE, type IncomeOneOff, type IncomeSourceDetail } from '../api';
import { Section } from '@/components/page/Section';
import { OneOffForm } from './OneOffForm';
import { OneOffRowActions } from './OneOffRowActions';
import '@/locales/income';

// The server lists the one-offs from `from` on; the earliest date it reads stands for « all of them ».
const ALL_FROM = '0001-01-01';

/**
 * Spec §4: the source's one-off income, newest first: the last twelve months as the page loaded them, or all of them
 * (`?oneOffs=all`, issue #22). The section owns its dialog (adding or correcting), its live region and where the focus
 * lands (`useRowDialog`); without write access it is a read-only table. An ended source still takes one-offs up to its end.
 */
export function OneOffsSection({ source, writable }: { source: IncomeSourceDetail; writable: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: RootSearch & IncomeDetailSearch = useSearch({ strict: false });
  const all = search.oneOffs === 'all';
  const everything = $api.useQuery('get', SOURCE, { params: { path: { id: source.id }, query: { from: ALL_FROM } } }, { enabled: all });
  const listed = all ? everything.data?.oneOffs : source.oneOffs;
  const choose = (next: string) =>
    void navigate({
      from: '/income/$id', to: '/income/$id', params: { id: source.id }, replace: true,
      search: (prev: RootSearch & IncomeDetailSearch) => ({ ...prev, oneOffs: next === 'all' ? ('all' as const) : undefined }),
    });
  const rows = [...(listed ?? [])].sort((a, b) => (a.occurredOn < b.occurredOn ? 1 : a.occurredOn > b.occurredOn ? -1 : 0));
  const row = useRowDialog(rows, 'one-off-menu');
  const { dialog, cancel } = row;
  // A source that turns read-only drops its open dialog, so it does not come back once writable again.
  useEffect(() => { if (!writable && dialog) cancel(); }, [writable, dialog, cancel]);
  const documents = useAnchoredDocuments(source.id).data?.items ?? [];
  const cols = writable ? 4 : 3;

  return (
    <Section title={t('income_detail.one_offs.title')} headingRef={row.headingRef} announcement={row.announcement}>
      {writable ? <Button ref={row.addButtonRef} type="button" variant="outline" onClick={() => row.open()}>{t('income_detail.one_offs.add')}</Button> : null}
      {writable && dialog ? (
        <FormDialog title={dialog.editing ? t('income_detail.one_offs.edit_title') : t('income_detail.one_offs.add_title')} onClose={row.cancel} onDone={row.saved}>
          {({ titleId, done }) => <OneOffForm source={source} editing={dialog.editing} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
      <Segmented aria-label={t('income_detail.one_offs.period')} value={all ? 'all' : 'recent'} onValueChange={choose}
        options={[{ value: 'recent', label: t('income_detail.one_offs.recent') }, { value: 'all', label: t('income_detail.one_offs.all') }]} />
      {all && everything.error ? <ProblemMessage problem={present(toProblem(everything.error))} /> : null}
      <Table aria-label={all ? t('income_detail.one_offs.table_all') : t('income_detail.one_offs.table')} aria-busy={all && everything.isPending ? true : undefined}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('income_detail.one_offs.date')}</TableHead>
            <TableHead className="text-right">{t('income_detail.one_offs.amount')}</TableHead>
            <TableHead>{t('income_detail.one_offs.note')}</TableHead>
            {writable ? <TableHead className="w-0"><span className="sr-only">{t('income_detail.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {listed && rows.length === 0 ? (
            <TableRow><TableCell colSpan={cols} className="text-muted-foreground">{all ? t('income_detail.one_offs.empty_all') : t('income_detail.one_offs.empty')}</TableCell></TableRow>
          ) : rows.map((f) => (
            <OneOffRow key={f.id} source={source} oneOff={f} docs={documents} writable={writable} row={row} />
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}

/** One row: it owns the attach writes (« Joindre un document »), so a refused upload shows in the note cell, where there is room. */
function OneOffRow({ source, oneOff, docs, writable, row }: {
  source: IncomeSourceDetail; oneOff: IncomeOneOff; docs: DocumentSummary[]; writable: boolean; row: ReturnType<typeof useRowDialog<IncomeOneOff>>;
}) {
  const writes = usePayslipWrites();
  return (
    <TableRow>
      <TableCell><LocalDate value={oneOff.occurredOn} /></TableCell>
      <TableCell className="text-right tabular-nums"><Money value={oneOff.amount} tabular /></TableCell>
      <TableCell className="whitespace-normal">
        {oneOff.note}<OneOffDocument source={source} oneOff={oneOff} docs={docs} /><PayslipProblem writes={writes} />
      </TableCell>
      {writable ? (
        <TableCell>
          <OneOffRowActions source={source} oneOff={oneOff} writes={writes} onEdit={() => row.open(oneOff)} onDeleteStarted={() => row.setAnnouncement('')} onDeleted={row.announce} onReloaded={() => row.headingRef.current?.focus()} />
        </TableCell>
      ) : null}
    </TableRow>
  );
}
