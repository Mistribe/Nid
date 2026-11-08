import type { TFunction } from 'i18next';
import type { DocumentSummary } from '@/api/documents';
import '@/locales/documents';

const KO = 1024;
const MO = 1024 * 1024;

/** A file size in Ko under a mega, in Mo above (1 decimal), French digits. Never under 1 Ko, so a small file does not read « 0 Ko ». */
export function fileSize(t: TFunction, bytes: number): string {
  if (bytes < MO) return t('documents_page.size_ko', { n: new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 0 }).format(Math.max(1, bytes / KO)) });
  return t('documents_page.size_mo', { n: new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1 }).format(bytes / MO) });
}

/** The roles a link can take, in the order the role select offers them. */
export const ROLES = ['lease', 'loan_offer', 'deed', 'tax_notice', 'statement', 'payslip', 'insurance', 'invoice', 'amortization_table', 'copro_call', 'copro_minutes', 'copro_accounts', 'contract', 'transaction_notice', 'request', 'advice', 'product_sheet', 'other'] as const;

export const roleText = (t: TFunction, role: string) => t(`documents_page.role.${role}`, { defaultValue: role });

/** The calendar day of an instant in the browser's time zone (yyyy-mm-dd), so a late upload is not shown on the next UTC day. */
export function localDay(instant: string): string {
  const d = new Date(instant);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** The date a document is shown and filtered by: its own, else the day it was uploaded (local day). */
export const shownDate = (d: DocumentSummary) => d.documentDate ?? localDay(d.uploadedAt);

/** A link's identity: two links to the same element share it whatever their role. */
export const linkKey = (l: { targetKind: string; targetId: string }) => `${l.targetKind}:${l.targetId}`;
