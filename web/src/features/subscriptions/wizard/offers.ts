import type { Path } from 'react-hook-form';
import type { components } from '@/api/schema';
import { toWireAmount } from '@/lib/amount';
import type { Unit, WizardValues } from './schema';
import '@/locales/subscriptions';

type PhaseInput = components['schemas']['PhaseInput'];
type PurchaseInput = components['schemas']['PurchaseInput'];
type GrantInput = components['schemas']['GrantInput'];

export type Offer = 'recurring' | 'prepaid' | 'trial' | 'gift' | 'bundle';
export const OFFERS: Offer[] = ['recurring', 'prepaid', 'trial', 'gift', 'bundle'];
export type StepName = 'type' | 'common' | 'offer' | 'then' | 'summary';

type Kind = Pick<WizardValues, 'offer' | 'trialVariant'>;

/** 1E2b1 design §2.1: every offer but the recurring one may be followed by a phase; the launch price must be. */
export const thenMode = (v: Kind): 'none' | 'optional' | 'required' =>
  v.offer === 'recurring' ? 'none' : v.offer === 'trial' && v.trialVariant === 'launch' ? 'required' : 'optional';
export const thenDefault = (offer: Offer): boolean => offer === 'prepaid' || offer === 'trial';
export const stepsFor = (offer: Offer): StepName[] =>
  offer === 'recurring' ? ['type', 'common', 'offer', 'summary'] : ['type', 'common', 'offer', 'then', 'summary'];

const pad = (n: number) => String(n).padStart(2, '0');
const daysIn = (year: number, month0: number) => new Date(Date.UTC(year, month0 + 1, 0)).getUTCDate();

/** `date` + `count` × `unit`, as NodaTime adds them: months and years keep the day, clamped to the month's last. */
export function addDuration(date: string, count: number, unit: Unit): string {
  const [y, m, d] = date.split('-').map(Number) as [number, number, number];
  if (unit === 'day' || unit === 'week') {
    const at = new Date(Date.UTC(y, m - 1, d + count * (unit === 'week' ? 7 : 1)));
    return `${at.getUTCFullYear()}-${pad(at.getUTCMonth() + 1)}-${pad(at.getUTCDate())}`;
  }
  const months = (m - 1) + count * (unit === 'year' ? 12 : 1);
  const year = y + Math.floor(months / 12);
  const month0 = ((months % 12) + 12) % 12;
  return `${year}-${pad(month0 + 1)}-${pad(Math.min(d, daysIn(year, month0)))}`;
}

const money = (amount: string, v: WizardValues) => ({ amount: toWireAmount(amount), currency: v.currency.trim().toUpperCase() });
const payerOf = (type: 'household' | 'person' | 'external', person: string) =>
  type === 'person' ? { type, personId: person } : { type, personId: null };
const grant = (count: string, unit: Unit, source: GrantInput['source'], startsOn: string | null = null,
  subscriptionId: string | null = null): GrantInput => ({ subscriptionId, duration: { count: Number(count), unit }, source, startsOn });

/** The 1E2a phase fields as a phase starting on `startsOn`. */
function phaseOf(v: WizardValues, startsOn: string, endsOn: string | null = null): PhaseInput {
  return {
    startsOn, endsOn,
    price: money(v.amount, v),
    every: { count: Number(v.everyCount), unit: v.everyUnit },
    covers: v.sameCovers ? null : { count: Number(v.coversCount), unit: v.coversUnit },
    maxCycles: v.maxCycles === '' ? null : Number(v.maxCycles),
    payer: payerOf(v.payerType, v.payerPerson),
  };
}

function purchase(v: WizardValues, kind: PurchaseInput['kind'], price: string, payer: PurchaseInput['payer'], grants: GrantInput[],
  allocatedAmount: string | null = null): PurchaseInput {
  return { date: v.offerDate, label: v.offerLabel.trim(), kind, price: money(price, v), payer, grants, allocatedAmount, note: null };
}

/** 1E2b1 design §4.2: the phases and purchases the chosen offer records. */
export function offerBody(v: WizardValues): { phases: PhaseInput[]; purchases: PurchaseInput[] } {
  const then = v.then ? [phaseOf(v, v.startsOn)] : [];
  const offerPayer = payerOf(v.offerPayerType, v.offerPayerPerson);
  switch (v.offer) {
    case 'recurring':
      return { phases: [phaseOf(v, v.startsOn)], purchases: [] };
    case 'prepaid':
      return {
        phases: then,
        purchases: [purchase(v, 'prepaid_term', v.offerAmount, offerPayer, [
          grant(v.paidCount, v.paidUnit, 'paid'),
          ...(v.bonusCount.trim() === '' ? [] : [grant(v.bonusCount, v.bonusUnit, 'bonus')]),
        ])],
      };
    case 'trial':
      if (v.trialVariant === 'launch') {
        const reduced: PhaseInput = { ...phaseOf(v, v.offerDate, v.nextStartsOn), price: money(v.launchAmount, v), maxCycles: Number(v.launchCycles) };
        return { phases: [reduced, phaseOf(v, v.nextStartsOn)], purchases: [] };
      }
      return {
        phases: then,
        purchases: [purchase(v, 'prepaid_term', '0', payerOf('household', ''), [grant(v.durationCount, v.durationUnit, 'trial', v.offerDate)])],
      };
    case 'gift':
      return {
        phases: then,
        purchases: [purchase(v, 'gift', v.giftReceived ? '0' : v.offerAmount, v.giftReceived ? payerOf('external', '') : offerPayer,
          [grant(v.durationCount, v.durationUnit, 'gift')])],
      };
    case 'bundle':
      return {
        phases: then,
        purchases: [purchase(v, 'bundle', v.offerAmount, offerPayer, [
          grant(v.durationCount, v.durationUnit, 'included'),
          ...v.others.map((o) => grant(o.count, o.unit, 'included', null, o.subscriptionId)),
        ], toWireAmount(v.allocated))],
      };
  }
}

const COMMON = ['displayName', 'visibility', 'owners', 'providerKey', 'users', 'note'];

/** The step that owns a server path, so a refusal brings the user back to it (Review Focus 4). */
export function stepOfPath(offer: Offer, trialVariant: 'free' | 'launch', path: string): StepName | undefined {
  if (COMMON.some((p) => path === p || path.startsWith(`${p}.`) || path.startsWith(`${p}[`))) return 'common';
  if (path.startsWith('purchases[')) return 'offer';
  const phase = /^phases\[(\d+)\]/.exec(path);
  if (!phase) return undefined;
  if (offer === 'recurring') return 'offer';
  if (offer === 'trial' && trialVariant === 'launch') return phase[1] === '0' ? 'offer' : 'then';
  return 'then';
}

const PHASE_FIELDS: Record<string, Path<WizardValues>> = {
  startsOn: 'startsOn', endsOn: 'startsOn', price: 'amount', 'price.amount': 'amount', 'price.currency': 'currency',
  every: 'everyCount', 'every.count': 'everyCount', 'every.unit': 'everyUnit', covers: 'coversCount', 'covers.count': 'coversCount',
  'covers.unit': 'coversUnit', maxCycles: 'maxCycles', payer: 'payerType', 'payer.type': 'payerType', 'payer.personId': 'payerPerson',
};

/** A server path as the form field it concerns, for the chosen offer; the rest shows above the form. */
export function offerFieldOf(path: string, v: Kind): Path<WizardValues> | undefined {
  const phase = /^phases\[(\d+)\]\.?(.*)$/.exec(path);
  if (phase) {
    const [, index, rest] = phase as unknown as [string, string, string];
    if (v.offer === 'trial' && v.trialVariant === 'launch') {
      if (index === '0' && (rest === 'price' || rest === 'price.amount')) return 'launchAmount';
      if (index === '0' && rest === 'maxCycles') return 'launchCycles';
      if ((index === '0' && rest === 'endsOn') || (index === '1' && rest === 'startsOn')) return 'nextStartsOn';
      if (index === '0' && rest === 'startsOn') return 'offerDate';
    }
    return PHASE_FIELDS[rest];
  }
  const bought = /^purchases\[0\]\.(.*)$/.exec(path)?.[1];
  if (bought === undefined) return undefined;
  const grantAt = /^grants\[(\d+)\](?:\.(.*))?$/.exec(bought);
  if (grantAt) {
    const index = Number(grantAt[1]);
    const rest = grantAt[2] ?? '';
    const unit = rest === 'duration.unit';
    if (v.offer === 'prepaid') return index === 0 ? (unit ? 'paidUnit' : 'paidCount') : (unit ? 'bonusUnit' : 'bonusCount');
    if (v.offer === 'bundle' && index > 0) {
      const row = index - 1;
      return rest === 'subscriptionId' ? `others.${row}.subscriptionId` : unit ? `others.${row}.unit` : `others.${row}.count`;
    }
    return unit ? 'durationUnit' : 'durationCount';
  }
  const fields: Record<string, Path<WizardValues>> = {
    date: 'offerDate', label: 'offerLabel', price: 'offerAmount', 'price.amount': 'offerAmount', 'price.currency': 'currency',
    payer: 'offerPayerType', 'payer.type': 'offerPayerType', 'payer.personId': 'offerPayerPerson', allocatedAmount: 'allocated',
  };
  return fields[bought];
}
