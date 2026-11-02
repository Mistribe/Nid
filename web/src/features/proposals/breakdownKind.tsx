import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { formatLocalDate } from '@/components/LocalDate';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { REGIONS, regionText, SECTORS, sectorText, sharePct } from '../investments/holdings/breakdown';
import type { KindUi } from './kindParts';
import '@/locales/investments';
import '@/locales/proposals';

type Part = { code: string; pct: number };
type Shown = { asOf: string; regions: Part[]; sectors?: Part[] | null };
type BreakdownPreview = { holdingName: string; proposed: Shown; current?: Shown | null };

const asBreakdown = (payload: unknown) => payload as { isin: string; asOf: string };

/** The proposed shares next to the current ones, the fixed codes in their display order; no sum is computed here. */
function Shares({ title, order, text, proposed, current }: {
  title: string; order: readonly string[]; text: (t: TFunction, code: string) => string; proposed: Part[]; current?: Part[] | null;
}) {
  const { t } = useTranslation();
  const known = new Set([...proposed, ...(current ?? [])].map((p) => p.code));
  const codes = [...order.filter((c) => known.has(c)), ...[...known].filter((c) => !order.includes(c))];
  const pct = (parts: Part[] | null | undefined, code: string) => { const p = parts?.find((x) => x.code === code); return p ? sharePct(p.pct) : '—'; };
  return (
    <Table>
      <caption className="text-left font-semibold">{title}</caption>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">{t('proposal.breakdown.part')}</TableHead>
          <TableHead scope="col" className="text-right">{t('proposal.breakdown.proposed')}</TableHead>
          <TableHead scope="col" className="text-right">{t('proposal.breakdown.current')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {codes.map((code) => (
          <TableRow key={code}>
            <TableCell>{text(t, code)}</TableCell>
            <TableCell className="text-right">{pct(proposed, code)}</TableCell>
            <TableCell className="text-right">{pct(current, code)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** Holdings exposure C5: « Répartition proposée », the proposed shares beside the current breakdown of the ISIN. */
function BreakdownPreviewView({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const p = preview as BreakdownPreview;
  return (
    <div className="space-y-3">
      <h2 className="text-base font-medium">{t('proposal.breakdown.title', { name: p.holdingName })}</h2>
      <p className="text-sm text-muted-foreground">
        {t('proposal.breakdown.dates', { proposed: formatLocalDate(p.proposed.asOf), current: p.current ? formatLocalDate(p.current.asOf) : '—' })}
      </p>
      <Shares title={t('breakdown.regions')} order={REGIONS} text={regionText} proposed={p.proposed.regions} current={p.current?.regions} />
      {p.proposed.sectors || p.current?.sectors ? (
        <Shares title={t('breakdown.sectors')} order={SECTORS} text={sectorText} proposed={p.proposed.sectors ?? []} current={p.current?.sectors} />
      ) : null}
    </div>
  );
}

/** The shares are the factsheet's: approved as proposed, or rejected. */
function BreakdownFields() {
  const { t } = useTranslation();
  return <p>{t('proposal.breakdown.as_proposed')}</p>;
}

export const breakdown: KindUi = {
  label: 'proposals.kind.breakdown',
  summary: (t, p) => t('proposals.summary.breakdown', {
    name: p.targetName ?? t('proposals.unknown_target'), isin: asBreakdown(p.editedPayload ?? p.payload).isin,
    date: formatLocalDate(asBreakdown(p.editedPayload ?? p.payload).asOf, 'short'),
  }),
  Preview: BreakdownPreviewView,
  schema: () => z.object({}),
  toForm: () => ({}),
  toPayload: () => null,
  field: () => undefined,
  Fields: BreakdownFields,
  link: (p) => ({ to: '/investments/$id', params: { id: p.targetItemId ?? '' } }),
};
