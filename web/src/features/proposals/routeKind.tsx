import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import strings from '@/locales/fr.proposals.json';
import { Table, TableBody } from '@/components/ui/table';
import { percent } from '@/lib/format';
import { DOCUMENTS } from '@/api/documents';
import { TAX_ASSESSMENTS, type TaxAssessment } from '../tax/assessments/api';
import { AssessmentChanges, AssessmentView } from '../tax/assessments/AssessmentView';
import { PERSONS } from '@/api/persons';
import type { ProposalView } from '@/api/proposals';
import { Row, type LinkTarget } from './kindParts';
import '@/locales/proposals';

/**
 * Spec 4D3: a proposal that replays a web write (`realestate.create_property`). One read-only screen for all of them: the
 * action, its subject and the fields proposed, in French and grouped (issue #290); approved as proposed or rejected, never edited.
 */
const actionOf = (kind: string) => kind.slice(kind.indexOf('.') + 1);
const moduleOf = (kind: string) => kind.slice(0, kind.indexOf('.'));

/** A kind the server replays as a web write: one whose action has a label. */
export const isRouteKind = (kind: string) => Object.hasOwn(strings.proposal.action, actionOf(kind));

type Payload = { id?: string; body?: Record<string, unknown> } & Record<string, unknown>;

const payloadOf = (p: ProposalView) => (p.editedPayload ?? p.payload) as Payload;

export const routeLabel = (t: TFunction, kind: string) => t(`proposal.action.${actionOf(kind) as keyof typeof strings.proposal.action}`);

/** Ids the household can read named: persons (a declarant, a child) and, when the kinds need them, documents. */
export function useNames(kinds: string[]): Map<string, string> {
  const { t } = useTranslation();
  const { $api } = useApi();
  const persons = $api.useQuery('get', PERSONS);
  const documents = $api.useQuery('get', DOCUMENTS, { params: { query: { scope: 'me' } } },
    { enabled: kinds.some((k) => moduleOf(k) === 'documents' || moduleOf(k) === 'tax') });
  // A notice is no item: a deletion's id is named by the notices the reader sees (issue #321).
  const notices = $api.useQuery('get', TAX_ASSESSMENTS, { params: { query: { scope: 'household' } } }, { enabled: kinds.some((k) => moduleOf(k) === 'tax') });
  // ponytail: an item's id (an envelope, a property) is not named, so it is hidden; the server would have to send the names it may show.
  return new Map([
    ...(persons.data ?? []).map((x) => [x.id, x.displayName] as const), ...(documents.data?.items ?? []).map((d) => [d.id, d.title] as const),
    ...(notices.data?.items ?? []).map((n) => [n.id, t('proposal.route.tax_year', { year: n.year })] as const),
  ]);
}

const isText = (v: unknown): v is string => typeof v === 'string';

/** What the action is about: its item, the name its body gives, a notice's year or a named id; null when nothing names it. */
export function routeSubject(t: TFunction, p: ProposalView, names: Map<string, string>): string | null {
  const payload = payloadOf(p);
  const body = payload.body ?? {};
  const named = [body.name, body.displayName, body.title].find(isText);
  const year = moduleOf(p.kind) === 'tax' && typeof body.year === 'number' ? t('proposal.route.tax_year', { year: body.year }) : undefined;
  return p.targetName ?? named ?? year ?? (isText(payload.id) ? names.get(payload.id) : undefined)
    ?? (p.targetItemId ? t('proposals.unknown_target') : null);
}

/** The detail's « Élément »: the subject, « Nouvel élément » for an unnamed creation, nothing otherwise. */
export const routeElement = (t: TFunction, p: ProposalView, names: Map<string, string>) =>
  routeSubject(t, p, names) ?? (actionOf(p.kind).startsWith('create_') ? t('proposal.route.new') : null);

export const routeSummary = (t: TFunction, p: ProposalView, names: Map<string, string>) =>
  [routeLabel(t, p.kind), routeSubject(t, p, names)].filter(Boolean).join(' — ');

/** The item's page: the target, or for a creation once approved the item it created. */
export function routeLink(p: ProposalView): LinkTarget | undefined {
  const id = p.targetItemId ?? (p.status === 'approved' ? p.appliedRefs[0] : undefined);
  const [module, action] = [moduleOf(p.kind), actionOf(p.kind)];
  if (!id || /routine|transfer|dependent|provider/.test(action)) return undefined;
  if (module === 'investments') return { to: '/investments/$id', params: { id } };
  if (module === 'income') return { to: '/income/$id', params: { id } };
  if (module === 'subscriptions') return { to: '/subscriptions/$id', params: { id } };
  if (module === 'realestate') return { to: /loan|bank_schedule/.test(action) ? '/loans/$id' : '/properties/$id', params: { id } };
  return undefined;
}

const isMoney = (v: unknown): v is { amount: string; currency: string } =>
  typeof v === 'object' && v !== null && Object.keys(v).length === 2 && 'amount' in v && 'currency' in v;

const FIELDS: Record<string, string> = strings.proposal.field;
const ENTRIES: Record<string, string> = strings.proposal.entry;
const VALUES: Record<string, Record<string, string>> = strings.proposal.value;
/** Technical: the household never needs them (issue #290). */
const HIDDEN = new Set(['version', 'rank']);
const isId = (key: string) => key === 'id' || key.endsWith('Id');
const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/;

/** `perCeilingNext` → « Per ceiling next »: a field the dictionary misses still reads as words, never as a path. */
const readable = (key: string) => {
  const words = key.replace(/([A-Z])/g, ' $1').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
};
const fieldLabel = (key: string) => FIELDS[key] ?? readable(key);

/** A value as the household reads it; null for what it never needs (an id nothing names). */
function shown(t: TFunction, key: string, v: unknown, names: Map<string, string>): string | null {
  if (isMoney(v)) return formatMoney(v);
  if (typeof v === 'boolean') return t(v ? 'proposal.route.yes' : 'proposal.route.no');
  // A share in basis points (10000 = all of it), shown as OwnerChips shows it.
  if (key === 'shareBp' && typeof v === 'number') return t('owner.pct', { value: percent(v / 100) });
  if (typeof v === 'number') return String(v);
  if (!isText(v)) return null;
  if (isId(key)) return names.get(v) ?? null;
  const value = VALUES[key]?.[v];
  if (value) return value;
  return ISO_DAY.test(v) ? formatLocalDate(v, 'short') : v;
}

type Group = { heading: string | null; rows: [string, string][] };

/** The fields as titled groups: one per array entry (« Déclarant 1 — Alex ») or nested object, a list of plain values on one line. */
function groups(t: TFunction, value: Record<string, unknown>, heading: string | null, names: Map<string, string>, out: Group[], skip?: string) {
  const group: Group = { heading, rows: [] };
  out.push(group);
  const within = (title: string) => (heading ? `${heading} · ${title}` : title);
  for (const [key, v] of Object.entries(value)) {
    if (key === skip || HIDDEN.has(key) || v === null || v === undefined) continue;
    if (Array.isArray(v) && v.some((e) => typeof e === 'object' && e !== null && !isMoney(e))) {
      v.forEach((entry: Record<string, unknown>, i) => {
        // The entry's name is its heading: a person's or a document's, or the name or label it carries.
        const by = Object.keys(entry).find((k) => isId(k) && isText(entry[k]) && names.has(entry[k]))
          ?? ['name', 'label', 'title'].find((k) => isText(entry[k]));
        const name = by ? (isId(by) ? names.get(entry[by] as string) : (entry[by] as string)) : undefined;
        groups(t, entry, within(`${ENTRIES[key] ?? fieldLabel(key)} ${i + 1}${name ? ` — ${name}` : ''}`), names, out, by);
      });
    } else if (Array.isArray(v)) {
      const line = v.map((e) => shown(t, key, e, names)).filter(isText).join(', ');
      if (line) group.rows.push([fieldLabel(key), line]);
    } else if (typeof v === 'object' && !isMoney(v)) {
      groups(t, v as Record<string, unknown>, within(fieldLabel(key)), names, out);
    } else {
      const line = shown(t, key, v, names);
      if (line !== null) group.rows.push([fieldLabel(key), line]);
    }
  }
  return out;
}

/**
 * A tax notice proposed (`add_tax_year`, `correct_tax_year`) reads as the tax page shows a recorded one: same figures, same
 * order, a column per declarant. The body has the view's field names (TaxEndpoints); a left-out issuedYear is year + 1 there too.
 */
function ProposedNotice({ id, body, before }: { id: string | undefined; body: Record<string, unknown>; before: TaxAssessment | undefined }) {
  const { $api } = useApi();
  const persons = $api.useQuery('get', PERSONS);
  const year = body.year as number;
  const notice = { ...body, id: id ?? '', version: 0, issuedYear: (body.issuedYear as number | undefined) ?? year + 1 } as TaxAssessment;
  return (
    <>
      {before ? <AssessmentChanges before={before} after={notice} persons={persons.data ?? []} /> : null}
      <AssessmentView assessment={notice} persons={persons.data ?? []} actions={false} />
    </>
  );
}

/** Issue #321: a deletion shows the notice it removes, as recorded. */
function RemovedNotice({ notice }: { notice: TaxAssessment }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const persons = $api.useQuery('get', PERSONS);
  return (
    <>
      <p role="note" className="text-sm text-muted-foreground">{t('proposal.route.removes_notice')}</p>
      <AssessmentView assessment={notice} persons={persons.data ?? []} actions={false} />
    </>
  );
}

/** `preview`: the server's `{ current }` for a correction or a deletion (issue #321), absent when it cannot be read. */
export function RoutePayload({ p, preview }: { p: ProposalView; preview?: unknown }) {
  const { t } = useTranslation();
  const names = useNames([p.kind]);
  const { body, ...route } = payloadOf(p);
  const current = (preview as { current?: unknown } | null | undefined)?.current;
  if (moduleOf(p.kind) === 'tax') {
    const recorded = current as TaxAssessment | undefined;
    if (typeof body?.year === 'number') return <ProposedNotice id={route.id} body={body} before={recorded} />;
    if (recorded) return <RemovedNotice notice={recorded} />;
  }
  // The route's own values (`year`, `on`) first; its `id` is the subject, its other ids named or hidden.
  // An entry whose only field is its name (a guarantee) keeps its heading.
  const shownGroups = groups(t, { ...route, ...body }, null, names, [], 'id').filter((g) => g.rows.length > 0 || g.heading);
  const replacesInsurance = actionOf(p.kind) === 'set_insurance';
  return (
    <section aria-label={t('proposal.route.fields')} className="flex max-w-2xl flex-col gap-4">
      <h2 className="text-base font-medium">{t('proposal.route.fields')}</h2>
      {replacesInsurance ? <p role="note" className="text-sm text-muted-foreground">{t('proposal.route.replaces_insurance')}</p> : null}
      {shownGroups.length === 0 ? <p>{t('proposal.route.no_fields')}</p> : shownGroups.map((g, i) => (
        <section key={g.heading ?? i} aria-label={g.heading ?? undefined}>
          {g.heading ? <h3 className="mb-1 text-sm font-medium">{g.heading}</h3> : null}
          {g.rows.length > 0 ? (
            <Table>
              <TableBody>{g.rows.map(([label, v], j) => <Row key={j} label={label}>{v}</Row>)}</TableBody>
            </Table>
          ) : null}
        </section>
      ))}
    </section>
  );
}
