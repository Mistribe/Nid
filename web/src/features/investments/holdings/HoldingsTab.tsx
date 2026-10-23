import { Link } from '@tanstack/react-router';
import { AlertTriangle, ChevronRightIcon, Plus } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { formatLocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { RowMenu } from '@/components/page/RowMenu';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Input } from '@/components/ui/input';
import { HOLDINGS, type EnvelopeDetail, type Holding } from '../api';
import { AnnualReturn, Direction, pct } from '../Figures';
import { directionOf } from '../format';
import { CloseHoldingDialog, HoldingDialog } from './HoldingDialog';
import { formatUnits, HOLDING_KINDS, holdingKindText, percent, warningText } from './holdingFormat';
import { MovementDialog } from './MovementDialog';
import { PriceCell } from './PriceCell';
import { SnapshotDialog } from './SnapshotDialog';
import { SwitchDialog } from './SwitchDialog';
import '@/locales/investments';

type Dialog =
  | { kind: 'holding'; editing?: Holding } | { kind: 'close'; holding: Holding } | { kind: 'movement'; holding: Holding }
  | { kind: 'snapshot' } | { kind: 'switch' };

/**
 * Issue #34 §7: the envelope's holdings at their latest snapshot, every figure as the server gave it. Without any
 * holding, an invitation to detail them. Loaded with the tab only, so its dialogs stay out of the page's chunk.
 */
export function HoldingsTab({ envelope, writable }: { envelope: EnvelopeDetail; writable: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', HOLDINGS, { params: { path: { id: envelope.id } } });
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('');
  const [showClosed, setShowClosed] = useState(false);
  const open = (next: Dialog) => { setAnnouncement(''); setDialog(next); };
  const done = (message: string) => { setDialog(null); setAnnouncement(message); };
  const close = () => setDialog(null);

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <div role="status" aria-busy="true" aria-label={t('holdings.loading')} className="h-40 shimmer rounded-md border bg-muted" />;

  const view = answer.data;
  const holdings = view.holdings;
  const openHoldings = holdings.filter((h) => !h.closedOn);
  const needle = query.trim().toLocaleLowerCase('fr');
  const shown = holdings.filter((h) => (showClosed || !h.closedOn) && (!kind || h.kind === kind)
    && (!needle || h.name.toLocaleLowerCase('fr').includes(needle) || h.isin?.toLocaleLowerCase('fr').includes(needle)));
  const kinds = HOLDING_KINDS.filter((k) => holdings.some((h) => h.kind === k));
  const nameOf = (id: string) => holdings.find((h) => h.id === id)?.name;
  const dash = <span className="text-muted-foreground">{t('investments.dash')}</span>;
  // D9: prices off, the tab shows nothing of them.
  const priced = holdings.some((h) => h.price);
  const pricesOn = view.estimate?.missingReason !== 'disabled';

  const est = view.estimate;
  const estimate = est ? (
    <div className="space-y-1">
      {est.value && est.asOf ? (
        <p className="flex justify-between gap-4 border-t pt-2 text-sm font-medium">
          <span>{t('holding_prices.estimate_at', { date: formatLocalDate(est.asOf) })}{est.converted ? <span className="ml-2 font-normal text-muted-foreground">{t('holding_prices.estimate_converted')}</span> : null}</span>
          <Money value={est.value} tabular />
        </p>
      ) : est.missingReason && est.missingReason !== 'disabled' ? (
        <p className="text-sm text-muted-foreground">{t(`holding_prices.missing.${est.missingReason}`, { defaultValue: '' })}</p>
      ) : null}
      {est.stale.length > 0 ? (
        <div className="text-sm">
          <h3 className="font-medium">{t('holding_prices.not_up_to_date')}</h3>
          <ul aria-label={t('holding_prices.not_up_to_date')} className="space-y-1">
            {est.stale.map((line) => (
              <li key={`${line.holdingId}-${line.reason}`} className="flex items-start gap-2">
                <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-status-warning" />
                {nameOf(line.holdingId)} : {t(`holding_prices.stale.${line.reason}`, { defaultValue: line.reason })}
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  ) : null;

  const dialogs = dialog ? (
    dialog.kind === 'holding' ? <HoldingDialog envelope={envelope} editing={dialog.editing} pricesOn={pricesOn} onClose={close} onDone={done} />
      : dialog.kind === 'close' ? <CloseHoldingDialog envelope={envelope} holding={dialog.holding} onClose={close} onDone={done} />
        : dialog.kind === 'movement' ? <MovementDialog envelope={envelope} holding={dialog.holding} onClose={close} onDone={done} />
          : dialog.kind === 'snapshot' ? <SnapshotDialog envelope={envelope} holdings={openHoldings} onClose={close} onDone={done} />
            : <SwitchDialog envelope={envelope} holdings={openHoldings} onClose={close} onDone={done} />
  ) : null;
  const status = <p role="status" aria-live="polite">{announcement}</p>;

  // One tree whatever the branch: the first holding swaps the invitation for the table, and an open dialog must survive it.
  return (
    <>
      {status}
      {holdings.length === 0 ? (
        <EmptyState title={t('holdings.empty_title')} body={t('holdings.empty_body')}
          action={writable ? <Button type="button" onClick={() => open({ kind: 'holding' })}>{t('holdings.detail')}</Button> : undefined} />
      ) : (
        <section aria-labelledby="holdings-title" className="space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 id="holdings-title" className="text-lg font-semibold">
              {view.holdingsAsOf ? t('holdings.title_at', { date: formatLocalDate(view.holdingsAsOf) }) : t('holdings.title')}
            </h2>
            {writable ? (
              <div className="flex flex-wrap gap-2">
                {openHoldings.length > 0 ? (
                  <Button type="button" onClick={() => open({ kind: 'snapshot' })}><Plus aria-hidden="true" />{t('holdings.snapshot.open')}</Button>
                ) : null}
                <Button type="button" variant="outline" onClick={() => open({ kind: 'holding' })}>{t('holdings.add')}</Button>
                {openHoldings.length > 1 ? (
                  <Button type="button" variant="outline" onClick={() => open({ kind: 'switch' })}>{t('holdings.switch.open')}</Button>
                ) : null}
              </div>
            ) : null}
          </div>
          {view.complete === false ? <p className="text-sm text-muted-foreground">{t('holdings.partial')}</p> : null}
          <div className="flex flex-wrap items-end gap-3">
            <Field label={t('holdings.search')}>
              {(control) => <Input {...control} type="search" value={query} onChange={(e) => setQuery(e.target.value)} />}
            </Field>
            {kinds.length > 1 ? (
              <div className="w-48">
                <Field label={t('holdings.kind_label')}>
                  {(control) => (
                    <SelectField {...control} value={kind} onValueChange={setKind}
                      options={[{ value: '', label: t('holdings.kind_all') }, ...kinds.map((k) => ({ value: k, label: holdingKindText(t, k) }))]} />
                  )}
                </Field>
              </div>
            ) : null}
            {openHoldings.length < holdings.length ? (
              <div className="pb-2"><SwitchField label={t('holdings.show_closed')} checked={showClosed} onCheckedChange={setShowClosed} /></div>
            ) : null}
          </div>
          {shown.length === 0 ? <p className="text-sm text-muted-foreground">{t('holdings.no_match')}</p> : (
            <SortableTable caption={t('holdings.table')} rows={shown} rowKey={(h) => h.id} initialSort={{ id: 'value', desc: true }}
              columns={[
                { id: 'name', header: t('holdings.name'), sort: (h) => h.name, cell: (h) => <HoldingLink envelope={envelope.id} holding={h} /> },
                { id: 'kind', header: t('holdings.kind_label'), sort: (h) => holdingKindText(t, h.kind), cell: (h) => holdingKindText(t, h.kind) },
                { id: 'units', header: t('holdings.units'), align: 'end', cell: (h) => (h.line?.units ? formatUnits(h.line.units) : dash) },
                ...(priced ? [{ id: 'latest', header: t('holding_prices.price'), align: 'end' as const,
                  cell: (h: Holding) => (h.price ? <PriceCell envelope={envelope.id} holding={h} writable={writable} /> : dash) }] : []),
                { id: 'price', header: t('holdings.unit_price'), align: 'end', cell: (h) => (h.line?.unitPrice ? formatUnits(h.line.unitPrice) : dash) },
                { id: 'value', header: t('holdings.value'), align: 'end', sort: (h) => amountKey(h.line?.value.amount), cell: (h) => (h.line ? <Money value={h.line.value} tabular /> : dash) },
                { id: 'weight', header: t('holdings.weight'), align: 'end', sort: (h) => h.line?.weightPct ?? -1,
                  cell: (h) => (h.line?.weightPct != null ? t('investments.pct', { value: percent(h.line.weightPct) }) : dash) },
                { id: 'gain', header: t('holdings.gain'), align: 'end', sort: (h) => amountKey(h.figures?.gain.amount), cell: (h) => (h.figures ? <Direction amount={h.figures.gain} /> : dash) },
                { id: 'gainPct', header: t('holdings.gain_pct'), align: 'end', sort: (h) => h.figures?.gainPct ?? Number.NEGATIVE_INFINITY,
                  cell: (h) => (h.figures?.gainPct != null ? t('investments.pct', { value: pct(h.figures.gainPct) }) : dash) },
                { id: 'annual', header: t('holdings.annual_return'), align: 'end', sort: (h) => h.figures?.annualReturnPct ?? Number.NEGATIVE_INFINITY,
                  cell: (h) => (h.figures?.annualReturnPct != null ? <AnnualReturn value={h.figures.annualReturnPct} /> : dash) },
                ...(writable ? [{
                  id: 'actions', header: t('envelope.actions'),
                  cell: (h: Holding) => (
                    <RowMenu label={t('holdings.row_menu', { name: h.name })} items={[
                      { label: t('holdings.edit'), onSelect: () => open({ kind: 'holding', editing: h }) },
                      ...(h.closedOn ? [] : [
                        { label: t('holdings.movement.open'), onSelect: () => open({ kind: 'movement', holding: h }) },
                        { label: t('holdings.close.open'), onSelect: () => open({ kind: 'close', holding: h }) },
                      ]),
                    ]} />
                  ),
                }] : []),
              ]}
              mobile={(h) => (
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <HoldingLink envelope={envelope.id} holding={h} />
                    <p className="text-sm text-muted-foreground">{holdingKindText(t, h.kind)}</p>
                    {h.price ? <div className="mt-1 text-sm"><PriceCell envelope={envelope.id} holding={h} writable={writable} /></div> : null}
                  </div>
                  <div className="text-right">
                    {h.line ? <Money value={h.line.value} tabular /> : dash}
                    {h.figures ? <p className="text-sm"><Direction amount={h.figures.gain} /></p> : null}
                  </div>
                </div>
              )} />
          )}
          {view.unallocated && directionOf(view.unallocated.amount) !== 'flat' ? (
            <p className="flex justify-between gap-4 border-t pt-2 text-sm">
              <span>{t('holdings.unallocated')}</span><Money value={view.unallocated} tabular />
            </p>
          ) : null}
          {estimate}
          {view.warnings.length > 0 ? (
            // Folded: a long history can carry hundreds of unit differences, which would dwarf the table (#47).
            <Collapsible className="space-y-2">
              <CollapsibleTrigger asChild>
                <Button type="button" variant="ghost" size="sm" className="group -ml-3">
                  <ChevronRightIcon aria-hidden="true" className="transition-transform group-data-[state=open]:rotate-90" />
                  <AlertTriangle aria-hidden="true" className="text-status-warning" />
                  {t('holdings.warnings_toggle', { count: view.warnings.length })}
                </Button>
              </CollapsibleTrigger>
              <CollapsibleContent>
              <ul aria-label={t('holdings.warnings')} className="space-y-1">
                {view.warnings.map((w, i) => (
                  <li key={`${w.code}-${w.holdingId ?? ''}-${w.on ?? ''}-${i}`} className="flex items-start gap-2 text-sm">
                    <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-status-warning" />{warningText(t, w, nameOf)}
                  </li>
                ))}
              </ul>
              </CollapsibleContent>
            </Collapsible>
          ) : null}
        </section>
      )}
      {dialogs}
    </>
  );
}

function HoldingLink({ envelope, holding }: { envelope: string; holding: Holding }) {
  const { t } = useTranslation();
  return (
    <span>
      <Link to="/investments/$id/holdings/$holdingId" params={{ id: envelope, holdingId: holding.id }} className="font-medium underline-offset-4 hover:underline">
        {holding.name}
      </Link>
      {holding.closedOn ? <span className="ml-2 text-sm text-muted-foreground">{t('holdings.closed')}</span> : null}
    </span>
  );
}

export default HoldingsTab;
