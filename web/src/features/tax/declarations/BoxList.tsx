import { Copy } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { toFieldAmount, trimAmount } from '@/lib/amount';
import { eur, Term } from '../ResultTable';
import '@/locales/tax';

export type BoxRow = { code: string; title: string; tip?: string; note?: string; owner?: ReactNode; amount: string };

/** The amount as typed on impots.gouv.fr: the server's string, without trailing zeros, comma as separator. */
export const copyText = (amount: string) => toFieldAmount(trimAmount(amount));

const isLine = (code: string) => /^\d+$/.test(code);

/** A form's rows: code, label, owner, amount and a button that copies the amount. A digits-only code is a 2044 line. */
export function BoxList({ label, rows }: { label: string; rows: BoxRow[] }) {
  const { t } = useTranslation();
  const copy = ({ code, amount }: BoxRow) => {
    const value = copyText(amount);
    const failed = () => toast.error(t('tax.declarations.copy_failed'));
    try {
      // Called synchronously in the click so the browser keeps the user gesture.
      (navigator.clipboard?.writeText(value) ?? Promise.reject(new Error('no clipboard'))).then(
        () => toast.success(isLine(code) ? t('tax.declarations.copied_line', { code, value }) : t('tax.declarations.copied_box', { code, value })),
        failed,
      );
    } catch {
      failed();
    }
  };
  return (
    <dl aria-label={label} className="divide-y">
      {rows.map((row, i) => {
        const line = isLine(row.code);
        const name = line ? t('tax.declarations.copy_line', { code: row.code, list: label }) : t('tax.declarations.copy_box', { code: row.code });
        return (
          <div key={`${row.code}-${i}`} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2">
            <dt className="rounded-md bg-muted px-1.5 font-mono text-xs">{row.code}</dt>
            <dt className="min-w-0 flex-1 basis-40 text-sm">
              <Term title={row.title} tip={row.tip} />
              {row.note && <p className="text-xs text-muted-foreground">{row.note}</p>}
            </dt>
            <dd className="flex items-center gap-3">
              {row.owner}
              <span className="tabular-nums">{eur(row.amount)}</span>
              <Button type="button" variant="ghost" size="icon" className="print:hidden" aria-label={name} onClick={() => copy(row)}>
                <Copy aria-hidden />
              </Button>
            </dd>
          </div>
        );
      })}
    </dl>
  );
}
