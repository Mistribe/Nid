import { useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { Link, type LinkProps } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { ModuleTile } from '@/components/ModuleTile';
import { tileOf, type Place } from '@/components/nav';
import { SectionCard } from '@/components/page/SectionCard';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import type tax from '@/locales/fr.tax.json';
import type { TaxNotice } from '@/api/tax';
import { eur } from './ResultTable';
import { noticeAmount, WARNINGS, warningText } from './format';
import '@/locales/tax';

export type NoticeItem = { key: string; name: string; amount?: string; detail?: string; link?: LinkProps };
export type NoticeGroup = { kind: string; tile: Place; heading: string; items: NoticeItem[]; continued?: boolean };

const FOLD = 5;
const CHILDCARE = ['childcare_from_statements', 'childcare_estimated'] as const;
const PER_TILE = ['per_over_ceiling', 'per_ceiling_estimated'];
const PROPERTY_TILE = ['micro_over_ceiling', 'pinel_requires_real'];
const NO_AMOUNT = ['per_without_contributions', 'savings_interest_unknown', 'other_currency'];

type Kind = { kind: keyof typeof tax.tax.notices.kind; codes: readonly string[]; tile: Place; link?: (id: string) => LinkProps };
const envelope = (id: string): LinkProps => ({ to: '/investments/$id', params: { id } });
const income = (id: string): LinkProps => ({ to: '/income/$id', params: { id } });
// The order the groups are told in; the server's `item` is the module's own object (TaxContribution).
const KINDS: readonly Kind[] = [
  { kind: 'missing_gross', codes: ['missing_gross'], tile: '/income', link: income },
  { kind: 'contribution_rate_differs', codes: ['contribution_rate_differs'], tile: '/income', link: income },
  { kind: 'unsupported_income', codes: ['unsupported_income'], tile: '/income', link: income },
  { kind: 'not_a_declarant', codes: ['not_a_declarant'], tile: '/income' },
  { kind: 'other_currency', codes: ['other_currency'], tile: '/income' },
  { kind: 'savings_interest', codes: ['savings_interest'], tile: '/investments', link: envelope },
  { kind: 'savings_interest_unknown', codes: ['savings_interest_unknown'], tile: '/investments', link: envelope },
  { kind: 'realised_gain', codes: ['realised_gain'], tile: '/investments', link: envelope },
  { kind: 'realised_gain_unknown', codes: ['realised_gain_unknown'], tile: '/investments', link: envelope },
  { kind: 'withdrawal_not_estimated', codes: ['withdrawal_not_estimated'], tile: '/investments', link: envelope },
  { kind: 'per_without_contributions', codes: ['per_without_contributions'], tile: '/investments', link: envelope },
  { kind: 'childcare', codes: CHILDCARE, tile: '/childcare' },
  { kind: 'home_employment', codes: ['home_employment_from_months'], tile: '/childcare' },
  { kind: 'rental_charges_removed', codes: ['rental_charges_removed'], tile: '/properties', link: (id) => ({ to: '/properties/$id', params: { id } }) },
  { kind: 'rental_fee_not_counted', codes: ['rental_fee_not_counted'], tile: '/loans', link: (id) => ({ to: '/loans/$id', params: { id } }) },
  { kind: 'rental_to_check', codes: ['rental_to_check'], tile: '/properties', link: (id) => ({ to: '/properties/$id', params: { id }, search: { tab: 'expenses' } }) },
];

function childcareItems(t: TFunction, notices: TaxNotice[], year: number): NoticeItem[] {
  // The server sends one notice per arrangement: its total, its validated and estimated months.
  return notices.map((n) => ({
    key: n.item,
    name: n.name,
    detail: t(n.code === 'childcare_estimated' ? 'tax.notices.childcare.estimated' : 'tax.notices.childcare.from_statements',
      { amount: eur(n.amount), validated: n.monthsValidated, months: (n.monthsValidated ?? 0) + (n.monthsEstimated ?? 0) }),
    link: { to: '/childcare/$id', params: { id: n.item }, search: { tab: 'summary', year: String(year) } },
  }));
}

/** #94: a nanny's tracked months, and for the current year how the rest of it is counted. */
function homeItems(t: TFunction, notices: TaxNotice[], year: number): NoticeItem[] {
  return notices.map((n) => ({
    key: n.item, name: n.name,
    detail: t(n.monthsEstimated ? 'tax.notices.home_employment.projected' : 'tax.notices.home_employment.recorded',
      { amount: eur(n.amount), validated: n.monthsValidated, estimated: n.monthsEstimated }),
    link: { to: '/childcare/nannies/$id', params: { id: n.item }, search: { year: String(year) } },
  }));
}

/** The warnings, then the notices by kind: the advice a kind shares is said once, each item keeps its name, figure and link. */
export function noticeGroups(t: TFunction, { notices, warnings = [], year, rulesYear }: {
  notices: TaxNotice[]; warnings?: readonly string[]; year: number; rulesYear?: number;
}): NoticeGroup[] {
  const told = new Set(warnings);
  const warned = [...WARNINGS.filter((w) => told.has(w)), ...[...told].filter((w) => !WARNINGS.some((k) => k === w))];
  const unknownWarnings = warned.filter((w) => !WARNINGS.some((k) => k === w));
  const groups: NoticeGroup[] = warned.filter((w) => !unknownWarnings.includes(w)).map((w) => ({
    kind: w, items: [], heading: warningText(t, w, rulesYear ?? year),
    tile: PER_TILE.includes(w) ? '/retirement' : PROPERTY_TILE.includes(w) ? '/properties' : '/tax',
  }));
  const item = (n: TaxNotice, link: ((id: string) => LinkProps) | undefined, i: number): NoticeItem => ({
    key: `${i}-${n.code}-${n.item}`, name: n.name, link: link?.(n.item),
    amount: NO_AMOUNT.includes(n.code) ? undefined : noticeAmount(t, n.code, n.amount, eur),
  });
  for (const k of KINDS) {
    const mine = notices.filter((n) => k.codes.includes(n.code));
    if (!mine.length) continue;
    groups.push({ kind: k.kind, tile: k.tile, heading: t(`tax.notices.kind.${k.kind}`),
      items: k.kind === 'childcare' ? childcareItems(t, mine, year) : k.kind === 'home_employment' ? homeItems(t, mine, year) : mine.map((n, i) => item(n, k.link, i)) });
  }
  const unknown = notices.filter((n) => !KINDS.some((k) => k.codes.includes(n.code)));
  if (unknown.length || unknownWarnings.length) groups.push({ kind: 'other', tile: '/tax', heading: t('tax.notices.kind.other'), items: unknown.map((n, i) => item(n, undefined, i)) });
  return groups;
}

/** The first `shown` rows (an item, or a group without items; a heading above items is no row). A group cut by the fold keeps its heading above, its rest continues below. */
export function foldGroups(groups: NoticeGroup[], shown: number): { shown: NoticeGroup[]; hidden: NoticeGroup[]; hiddenCount: number } {
  const head: NoticeGroup[] = [];
  const hidden: NoticeGroup[] = [];
  let room = shown;
  for (const g of groups) {
    const rows = Math.max(1, g.items.length);
    if (rows <= room) { head.push(g); room -= rows; continue; }
    if (g.items.length && room > 0) {
      head.push({ ...g, items: g.items.slice(0, room) });
      hidden.push({ ...g, items: g.items.slice(room), continued: true });
    } else hidden.push(g);
    room = 0;
  }
  return { shown: head, hidden, hiddenCount: hidden.reduce((n, g) => n + Math.max(1, g.items.length), 0) };
}

function Groups({ groups }: { groups: NoticeGroup[] }) {
  const { t } = useTranslation();
  return (
    <ul className="space-y-3">
      {groups.map((g) => {
        const { icon } = tileOf(g.tile);
        return (
          <li key={`${g.kind}${g.continued ? '-more' : ''}`}>
            {g.continued ? null : (
              <div className="flex items-start gap-2 text-sm">
                <ModuleTile icon={icon} tile={tileOf(g.tile).tile} size="sm" />
                <span className="min-w-0">{g.heading}</span>
              </div>
            )}
            {g.items.length ? (
              <ul className={`${g.continued ? '' : 'mt-1.5 '}space-y-1 pl-[34px] text-sm`}>
                {g.items.map((i) => (
                  <li key={i.key} className="flex flex-wrap items-baseline gap-x-2">
                    <span className="min-w-0 mr-auto">
                      {i.link ? <Link {...i.link} className="underline underline-offset-2">{i.name}</Link> : i.name}
                      {i.detail ? (
                        <>
                          <span className="text-muted-foreground"> : {i.detail}</span>
                          {i.link ? <> <Link {...i.link} className="underline underline-offset-2">{t('tax.childcare_summary', { year: (i.link.search as { year?: string }).year })}</Link></> : null}
                        </>
                      ) : null}
                    </span>
                    {i.amount ? <span className="tabular-nums">{i.amount}</span> : null}
                  </li>
                ))}
              </ul>
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}

/** « À savoir »: what the estimate could not count or assumed, by kind. Nothing when the server says nothing. */
export function TaxNotices({ notices, warnings, year, rulesYear, title }: {
  notices: TaxNotice[]; warnings?: readonly string[]; year: number; rulesYear?: number; title?: string;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const folded = foldGroups(noticeGroups(t, { notices, warnings, year, rulesYear }), FOLD);
  if (!folded.shown.length) return null;
  return (
    <SectionCard title={title ?? t('tax.notes')} icon={<AlertTriangle aria-hidden className="size-4 text-status-warning" />}>
      <Collapsible open={open} onOpenChange={setOpen} className="space-y-3">
        <Groups groups={folded.shown} />
        {folded.hidden.length ? (
          <>
            <CollapsibleContent className={folded.hidden[0]?.continued ? '-mt-2' : undefined}><Groups groups={folded.hidden} /></CollapsibleContent>
            <CollapsibleTrigger className="text-sm underline underline-offset-2">
              {open ? t('tax.notices.less') : t('tax.notices.more', { count: folded.hiddenCount })}
            </CollapsibleTrigger>
          </>
        ) : null}
      </Collapsible>
    </SectionCard>
  );
}
