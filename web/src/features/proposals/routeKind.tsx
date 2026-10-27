import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import strings from '@/locales/fr.proposals.json';
import { Table, TableBody } from '@/components/ui/table';
import type { ProposalView } from './api';
import { Row, type LinkTarget } from './kindParts';
import '@/locales/proposals';

/**
 * Spec 4D3: a proposal that replays a web write (`realestate.create_property`). One read-only screen for all of them: the
 * action, its item and the fields proposed; approved as proposed or rejected, never edited.
 */
const actionOf = (kind: string) => kind.slice(kind.indexOf('.') + 1);

/** A kind the server replays as a web write: one whose action has a label. */
export const isRouteKind = (kind: string) => Object.hasOwn(strings.proposal.action, actionOf(kind));

type Payload = { id?: string; body?: Record<string, unknown> } & Record<string, unknown>;

const payloadOf = (p: ProposalView) => (p.editedPayload ?? p.payload) as Payload;

export const routeLabel = (t: TFunction, kind: string) => t(`proposal.action.${actionOf(kind) as keyof typeof strings.proposal.action}`);

/** The item's name, or for a creation the name it will have. */
function nameOf(t: TFunction, p: ProposalView): string {
  const body = payloadOf(p).body ?? {};
  const named = [body.name, body.displayName].find((v): v is string => typeof v === 'string');
  return p.targetName ?? named ?? t('proposals.unknown_target');
}

export const routeSummary = (t: TFunction, p: ProposalView) => `${routeLabel(t, p.kind)} — ${nameOf(t, p)}`;

/** The item's page: the target, or for a creation once approved the item it created. */
export function routeLink(p: ProposalView): LinkTarget | undefined {
  const id = p.targetItemId ?? (p.status === 'approved' ? p.appliedRefs[0] : undefined);
  const [module, action] = [p.kind.split('.')[0], actionOf(p.kind)];
  if (!id || /routine|transfer|dependent|provider/.test(action)) return undefined;
  if (module === 'investments') return { to: '/investments/$id', params: { id } };
  if (module === 'income') return { to: '/income/$id', params: { id } };
  if (module === 'subscriptions') return { to: '/subscriptions/$id', params: { id } };
  if (module === 'realestate') return { to: /loan|bank_schedule/.test(action) ? '/loans/$id' : '/properties/$id', params: { id } };
  return undefined;
}

const isMoney = (v: unknown): v is { amount: string; currency: string } =>
  typeof v === 'object' && v !== null && Object.keys(v).length === 2 && 'amount' in v && 'currency' in v;

/** Every value of the payload as one line, `owners[0].shareBp` style; an amount with its currency stays one line. */
function lines(value: unknown, path: string, out: [string, string][]) {
  if (isMoney(value)) out.push([path, formatMoney(value)]);
  else if (Array.isArray(value)) value.forEach((v, i) => lines(v, `${path}[${i}]`, out));
  else if (typeof value === 'object' && value !== null) Object.entries(value).forEach(([k, v]) => lines(v, path ? `${path}.${k}` : k, out));
  else if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') out.push([path, String(value)]);
  return out;
}

export function RoutePayload({ p }: { p: ProposalView }) {
  const { t } = useTranslation();
  const { body, ...route } = payloadOf(p);
  // The target is the « Élément » line already; a line's id (`flowId`), a version and an id that is no target (a document's) stay.
  const rows = [...lines(Object.fromEntries(Object.entries(route).filter(([k]) => k !== 'id' || !p.targetItemId)), '', []), ...lines(body ?? {}, '', [])];
  return (
    <section aria-label={t('proposal.route.fields')} className="max-w-2xl">
      <h2 className="mb-2 text-base font-medium">{t('proposal.route.fields')}</h2>
      {rows.length === 0 ? <p>{t('proposal.route.no_fields')}</p> : (
        <Table>
          <TableBody>{rows.map(([label, v]) => <Row key={label} label={label}>{v}</Row>)}</TableBody>
        </Table>
      )}
    </section>
  );
}
