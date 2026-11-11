import { useId, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { FieldControl } from '@/components/form/Field';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { TaxEstimate } from '@/api/tax';
import '@/locales/tax';

type Adult = TaxEstimate['adults'][number];
/** A cell's control: named by its row's label and its adult's column, described by the row's hint and the cell's error. */
export type GridControl = FieldControl & { 'aria-labelledby': string };
export type GridRow = {
  key: string; label: string; hint?: string; /** An InfoTip beside the label. */ tip?: ReactNode; error?: (adultId: string) => string | undefined;
  cell: (adult: Adult, control: GridControl) => ReactNode;
};

/** The fields each declarant fills, one row per field and one column per adult (spec §3): the two adults side by side. */
export function DeclarantGrid({ label, adults, rows }: { label: string; adults: Adult[]; rows: GridRow[] }) {
  const { t } = useTranslation();
  const id = useId();
  const head = (adult: Adult) => `${id}-${adult.id}`;
  return (
    <div className="rounded-xl border bg-card">
      <Table aria-label={label}>
        <TableHeader>
          <TableRow>
            <TableHead className="min-w-48"><span className="sr-only">{t('tax.profile.field')}</span></TableHead>
            {adults.map((a) => <TableHead key={a.id} id={head(a)} className="min-w-48 text-foreground">{a.displayName}</TableHead>)}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => {
            const labelId = `${id}-${row.key}`;
            const hintId = `${labelId}-hint`;
            return (
              <TableRow key={row.key} className="hover:bg-transparent">
                <TableHead scope="row" className="h-auto whitespace-normal py-3 align-top font-normal">
                  {row.tip ? (
                    <span className="flex items-center gap-1.5"><span id={labelId} className="font-medium text-foreground">{row.label}</span>{row.tip}</span>
                  ) : <span id={labelId} className="block font-medium text-foreground">{row.label}</span>}
                  {row.hint ? <span id={hintId} className="block text-sm text-muted-foreground">{row.hint}</span> : null}
                </TableHead>
                {adults.map((a) => {
                  const error = row.error?.(a.id);
                  const errorId = `${labelId}-${a.id}-error`;
                  const describedBy = [row.hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ');
                  const control: GridControl = { id: `${labelId}-${a.id}`, 'aria-labelledby': `${labelId} ${head(a)}` };
                  if (describedBy) control['aria-describedby'] = describedBy;
                  if (error) control['aria-invalid'] = true;
                  return (
                    <TableCell key={a.id} className="space-y-1 whitespace-normal py-3 align-top">
                      {row.cell(a, control)}
                      {error ? <span id={errorId} className="block text-sm text-destructive">{error}</span> : null}
                    </TableCell>
                  );
                })}
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}
