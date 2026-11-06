// Writes the demo household as nid.data files (docs/import-format.md), to fill the screens of a local stack for a visual review.
// Fictional figures. alice.nid.json is imported by alice (both adults' shared items and hers), bob.nid.json by bob (his own).
//   node deploy/local/demo/generate.mjs   (the checked-in files, which DemoFileTests imports; load.mjs calls household() itself)
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const LAST = '2026-10-28'; // The last fact of the files: no valuation may be in the future of the stack's calendar.

// A deterministic jitter, so that the files only change when this script does.
let seed;
const noise = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648 - 0.5) * 2;

const eur = (amount) => ({ amount: amount.toFixed(2), currency: 'EUR' });
const pad = (n) => String(n).padStart(2, '0');
const date = (y, m, d) => {
  const at = new Date(Date.UTC(y, m - 1, d));
  return `${at.getUTCFullYear()}-${pad(at.getUTCMonth() + 1)}-${pad(at.getUTCDate())}`;
};
const endOfMonth = (y, m) => date(y, m + 1, 0);
/** Each month from (y, m) up to the last fact's month, as [year, month]. */
function months(y, m) {
  const out = [];
  for (let at = [y, m]; `${at[0]}-${pad(at[1])}` <= LAST.slice(0, 7); at = at[1] === 12 ? [at[0] + 1, 1] : [at[0], at[1] + 1]) out.push(at);
  return out;
}
const mine = (person) => [{ person, share_bp: 10000 }];
const halves = (a, b) => [{ person: a, share_bp: 5000 }, { person: b, share_bp: 5000 }];
const every = (count, unit) => ({ count, unit });

/** Month-end valuations of an envelope that grows by `monthly` contributions and `rate` a month, with a little noise. */
function series(item, from, start, { monthly = 0, rate = 0, jitter = 0, until } = {}) {
  const valuations = [];
  let value = start;
  for (const [y, m] of months(...from)) {
    const as_of = endOfMonth(y, m) > LAST ? LAST : endOfMonth(y, m);
    if (until && as_of > until) break;
    value = value * (1 + rate + jitter * noise()) + monthly;
    valuations.push({ id: `val-${item.replace(/^env-/, '')}-${as_of.slice(0, 7)}`, item, as_of, amount: eur(value) });
  }
  return valuations;
}

function alice() {
  const A = 'adult-1', B = 'adult-2';
  const persons = [
    { ref: A, kind: 'adult', display_name: 'Alice Dev', email: 'alice@example.test' },
    { ref: B, kind: 'adult', display_name: 'Bob Dev', email: 'bob@example.test' },
    { ref: 'dependent-1', kind: 'dependent', display_name: 'Léa' },
  ];

  // Subscriptions: custom providers under the one label a fresh stack holds (`other`): the catalog is synced from the Internet.
  const providers = [
    { key: 'demo-spotify', name: 'Spotify', website: 'https://www.spotify.com', label_keys: ['other'] },
    { key: 'demo-netflix', name: 'Netflix', website: 'https://www.netflix.com', label_keys: ['other'] },
    { key: 'demo-box', name: 'Fibre maison', label_keys: ['other'] },
    { key: 'demo-mobile', name: 'Forfait mobile', label_keys: ['other'] },
    { key: 'demo-icloud', name: 'iCloud+', website: 'https://www.apple.com/icloud/', label_keys: ['other'] },
    { key: 'demo-vpn', name: 'NordVPN', website: 'https://nordvpn.com', label_keys: ['other'] },
    { key: 'demo-disney', name: 'Disney+', website: 'https://www.disneyplus.com', label_keys: ['other'] },
    { key: 'demo-assurance', name: 'Assurance habitation', label_keys: ['other'] },
  ];
  const sub = (id, provider_key, name, ownership, extra = {}) => ({ id, provider_key, name, ownership, auto_renew: true, ...extra });
  const subscriptions = [
    sub('sub-spotify', 'demo-spotify', 'Spotify Premium Duo', halves(A, B), { service_users: [A, B] }),
    sub('sub-netflix', 'demo-netflix', 'Netflix Standard', halves(A, B), { service_users: [A, B, 'dependent-1'] }),
    sub('sub-box', 'demo-box', 'Box fibre', halves(A, B)),
    sub('sub-mobile-alice', 'demo-mobile', 'Forfait mobile 100 Go', mine(A), { visibility: 'private', service_users: [A] }),
    sub('sub-icloud', 'demo-icloud', 'iCloud+ 200 Go', mine(A), { service_users: [A, B] }),
    sub('sub-vpn', 'demo-vpn', 'NordVPN', mine(A), { service_users: [A] }),
    sub('sub-disney', 'demo-disney', 'Disney+', halves(A, B), { service_users: ['dependent-1'], auto_renew: false, cancelled_at: '2026-02-14' }),
    sub('sub-home-insurance', 'demo-assurance', 'Assurance habitation', halves(A, B)),
  ];
  const household = { type: 'household' };
  const billing_phases = [
    { id: 'ph-spotify-1', subscription_id: 'sub-spotify', starts_on: '2023-03-12', ends_on: '2025-07-12', price: eur(14.99), every: every(1, 'month'), payer: household },
    { id: 'ph-spotify-2', subscription_id: 'sub-spotify', starts_on: '2025-07-12', ends_on: null, price: eur(17.99), every: every(1, 'month'), payer: household },
    { id: 'ph-netflix-1', subscription_id: 'sub-netflix', starts_on: '2022-11-05', ends_on: '2025-01-05', price: eur(13.49), every: every(1, 'month'), payer: household },
    { id: 'ph-netflix-2', subscription_id: 'sub-netflix', starts_on: '2025-01-05', ends_on: null, price: eur(14.99), every: every(1, 'month'), payer: household },
    { id: 'ph-box-1', subscription_id: 'sub-box', starts_on: '2024-02-20', ends_on: '2025-02-20', price: eur(24.99), every: every(1, 'month'), payer: household, note: 'Prix de lancement la première année' },
    { id: 'ph-box-2', subscription_id: 'sub-box', starts_on: '2025-02-20', ends_on: null, price: eur(39.99), every: every(1, 'month'), payer: household },
    { id: 'ph-mobile-alice', subscription_id: 'sub-mobile-alice', starts_on: '2024-09-01', ends_on: null, price: eur(9.99), every: every(1, 'month'), payer: { type: 'person', person: A } },
    { id: 'ph-icloud', subscription_id: 'sub-icloud', starts_on: '2023-01-15', ends_on: null, price: eur(2.99), every: every(1, 'month'), payer: { type: 'person', person: A } },
    { id: 'ph-disney', subscription_id: 'sub-disney', starts_on: '2024-02-14', ends_on: '2026-02-14', price: eur(89.90), every: every(1, 'year'), payer: household },
    { id: 'ph-home-insurance', subscription_id: 'sub-home-insurance', starts_on: '2021-09-01', ends_on: null, price: eur(312.40), every: every(1, 'year'), payer: household },
  ];
  const purchases = [
    {
      id: 'pu-vpn', date: '2025-11-03', label: 'NordVPN 2 ans + 3 mois offerts', kind: 'prepaid_term', price: eur(118.03), payer: { type: 'person', person: A },
      grants: [
        { subscription_id: 'sub-vpn', duration: every(24, 'month'), source: 'paid', starts_on: '2025-11-03' },
        { subscription_id: 'sub-vpn', duration: every(3, 'month'), source: 'bonus' },
      ],
    },
    {
      id: 'pu-spotify-gift', date: '2025-12-24', label: 'Carte cadeau Spotify', kind: 'gift', price: eur(0), payer: { type: 'external' },
      grants: [{ subscription_id: 'sub-spotify', duration: every(3, 'month'), source: 'gift' }],
    },
  ];

  // Envelopes, their month-end values and their flows.
  const envelopes = [
    { id: 'env-joint', name: 'Compte joint', kind: 'checking', institution: 'Banque Exemple', account_hint: '4821', opened_on: '2019-06-01', currency: 'EUR', ownership: halves(A, B) },
    { id: 'env-livret-a', name: 'Livret A', kind: 'savings', institution: 'Banque Exemple', opened_on: '2016-04-10', currency: 'EUR', ownership: mine(A) },
    { id: 'env-pea', name: 'PEA', kind: 'pea', institution: 'Courtier en ligne', account_hint: '0937', opened_on: '2020-01-15', currency: 'EUR', ownership: mine(A), visibility: 'private' },
    { id: 'env-av', name: 'Assurance-vie', kind: 'life_insurance', institution: 'Assureur Exemple', opened_on: '2022-03-01', currency: 'EUR', ownership: mine(A) },
    { id: 'env-cto', name: 'Compte-titres commun', kind: 'brokerage', institution: 'Courtier en ligne', opened_on: '2023-05-02', currency: 'EUR', ownership: [{ person: A, share_bp: 7000 }, { person: B, share_bp: 3000 }] },
    { id: 'env-old-livret', name: 'Ancien livret jeune', kind: 'savings', institution: 'Banque Exemple', opened_on: '2012-02-01', closed_on: '2025-03-31', currency: 'EUR', ownership: mine(A) },
  ];
  const valuations = [
    ...series('env-joint', [2024, 11], 3200, { jitter: 0.25 }),
    ...series('env-livret-a', [2024, 11], 14200, { monthly: 150, rate: 0.002 }),
    ...series('env-pea', [2024, 11], 31500, { monthly: 300, rate: 0.006, jitter: 0.03 }),
    ...series('env-cto', [2024, 11], 8400, { monthly: 100, rate: 0.005, jitter: 0.035 }),
    ...series('env-old-livret', [2024, 11], 2300, { rate: 0.002, until: '2025-02-28' }),
  ];
  const flows = [
    { id: 'fl-joint-open', item: 'env-joint', occurred_on: '2019-06-01', kind: 'opening', amount: eur(1500) },
    { id: 'fl-livret-open', item: 'env-livret-a', occurred_on: '2016-04-10', kind: 'opening', amount: eur(5000) },
    { id: 'fl-pea-open', item: 'env-pea', occurred_on: '2020-01-15', kind: 'opening', amount: eur(10000) },
    { id: 'fl-cto-open', item: 'env-cto', occurred_on: '2023-05-02', kind: 'opening', amount: eur(5000) },
    { id: 'fl-old-open', item: 'env-old-livret', occurred_on: '2012-02-01', kind: 'opening', amount: eur(1600) },
    { id: 'fl-old-close', item: 'env-old-livret', occurred_on: '2025-03-31', kind: 'withdrawal', amount: eur(2318.4), note: 'Clôture, versé sur le Livret A' },
    { id: 'fl-livret-from-old', item: 'env-livret-a', occurred_on: '2025-03-31', kind: 'contribution', amount: eur(2318.4), category: 'transfer' },
    { id: 'fl-pea-fees-2025', item: 'env-pea', occurred_on: '2025-12-31', kind: 'expense', amount: eur(18.9), category: 'fees' },
  ];
  for (const [y, m] of months(2024, 11)) {
    flows.push({ id: `fl-livret-${y}-${pad(m)}`, item: 'env-livret-a', occurred_on: date(y, m, 5), kind: 'contribution', amount: eur(150) });
    flows.push({ id: `fl-pea-${y}-${pad(m)}`, item: 'env-pea', occurred_on: date(y, m, 8), kind: 'contribution', amount: eur(300) });
    flows.push({ id: `fl-cto-${y}-${pad(m)}`, item: 'env-cto', occurred_on: date(y, m, 10), kind: 'contribution', amount: eur(100) });
  }

  // The life insurance in detail: a euro fund and a world ETF, a contribution split between them each quarter, quarterly statements.
  const holdings = [
    { id: 'h-av-euro', item: 'env-av', name: 'Fonds euros', kind: 'euro_fund' },
    { id: 'h-av-world', item: 'env-av', name: 'ETF MSCI World', kind: 'etf', isin: 'IE00B4L5Y983' },
  ];
  const holding_snapshots = [];
  const holding_movements = [];
  flows.push({ id: 'fl-av-open', item: 'env-av', occurred_on: '2022-03-01', kind: 'opening', amount: eur(12000) });
  holding_movements.push(
    { id: 'hm-av-open-euro', holding: 'h-av-euro', occurred_on: '2022-03-01', kind: 'allocation_in', amount: eur(6000), envelope_flow: 'fl-av-open' },
    { id: 'hm-av-open-world', holding: 'h-av-world', occurred_on: '2022-03-01', kind: 'allocation_in', units: '80', unit_price: '75', amount: eur(6000), envelope_flow: 'fl-av-open' },
  );
  let euro = 6000, units = 80, price = 75;
  for (const [y, m] of months(2024, 12).filter(([, m]) => m % 3 === 0)) {
    const on = date(y, m, 15);
    const id = `fl-av-${y}-${pad(m)}`;
    flows.push({ id, item: 'env-av', occurred_on: on, kind: 'contribution', amount: eur(1000) });
    const bought = Math.round((500 / price) * 10000) / 10000;
    holding_movements.push(
      { id: `hm-${id}-euro`, holding: 'h-av-euro', occurred_on: on, kind: 'allocation_in', amount: eur(500), envelope_flow: id },
      { id: `hm-${id}-world`, holding: 'h-av-world', occurred_on: on, kind: 'allocation_in', units: String(bought), unit_price: price.toFixed(2), amount: eur(bought * price), envelope_flow: id },
    );
    euro = euro * 1.006 + 500;
    units += bought;
    price = Math.round(price * (1.025 + 0.04 * noise()) * 100) / 100;
    const as_of = endOfMonth(y, m) > LAST ? LAST : endOfMonth(y, m);
    const world = Math.round(units * price * 100) / 100;
    holding_snapshots.push({
      id: `hs-av-${as_of.slice(0, 7)}`, item: 'env-av', as_of,
      lines: [
        { holding: 'h-av-euro', value: eur(euro) },
        { holding: 'h-av-world', units: units.toFixed(4), unit_price: price.toFixed(2), value: eur(Number((units.toFixed(4) * price).toFixed(2))) },
      ],
    });
    valuations.push({ id: `val-av-${as_of.slice(0, 7)}`, item: 'env-av', as_of, amount: eur(euro + Number((units.toFixed(4) * price).toFixed(2))) });
    void world;
  }

  // Real estate: the family home (both adults, two loans) and Alice's let studio (one loan, an early repayment).
  const properties = [
    {
      id: 'prop-home', name: 'Maison de Nantes', property_type: 'primary_residence', currency: 'EUR', purchase_date: '2021-09-01',
      purchase_price: eur(320000), notary_fees: eur(24500), agency_fees: eur(9000), surface_m2: '112', location_label: 'Nantes',
      energy_class: 'C', ownership: halves(A, B), visibility: 'shared',
    },
    {
      id: 'prop-studio', name: 'Studio Lyon 7e', property_type: 'rental', currency: 'EUR', purchase_date: '2019-05-03',
      purchase_price: eur(135000), notary_fees: eur(10200), surface_m2: '24.5', location_label: 'Lyon 7e', rental_starts_on: '2019-06-01',
      ownership: mine(A),
    },
  ];
  valuations.push(
    { id: 'val-home-2023', item: 'prop-home', as_of: '2023-12-31', amount: eur(338000) },
    { id: 'val-home-2024', item: 'prop-home', as_of: '2024-12-31', amount: eur(331000) },
    { id: 'val-home-2025', item: 'prop-home', as_of: '2025-12-31', amount: eur(342000) },
    { id: 'val-studio-2023', item: 'prop-studio', as_of: '2023-12-31', amount: eur(149000) },
    { id: 'val-studio-2024', item: 'prop-studio', as_of: '2024-12-31', amount: eur(146500) },
    { id: 'val-studio-2025', item: 'prop-studio', as_of: '2025-12-31', amount: eur(151000) },
  );
  const property_recurring_costs = [
    { id: 'cost-home-tax', property: 'prop-home', category: 'property_tax', amount: eur(1840), every: every(1, 'year'), starts_on: '2021-10-15' },
    { id: 'cost-home-energy', property: 'prop-home', category: 'energy', amount: eur(145), every: every(1, 'month'), starts_on: '2021-09-01' },
    { id: 'cost-studio-tax', property: 'prop-studio', category: 'property_tax', amount: eur(720), every: every(1, 'year'), starts_on: '2019-10-15' },
    { id: 'cost-studio-condo', property: 'prop-studio', category: 'condo_fees', amount: eur(285), every: every(3, 'month'), starts_on: '2019-07-01', note: 'Appels de fonds du syndic' },
    { id: 'cost-studio-insurance', property: 'prop-studio', category: 'insurance', amount: eur(96), every: every(1, 'year'), starts_on: '2019-05-03', note: 'Propriétaire non occupant' },
  ];
  const tenancies = [
    { id: 'ten-studio-1', property: 'prop-studio', label: 'Studio meublé', starts_on: '2019-06-01', ends_on: '2023-06-01', rent_monthly: eur(610), charges_monthly: eur(40), deposit: eur(1220) },
    { id: 'ten-studio-2', property: 'prop-studio', label: 'Studio meublé', starts_on: '2023-07-15', rent_monthly: eur(665), charges_monthly: eur(45), deposit: eur(1330), indexed: true, irl_reference_quarter: 2 },
  ];
  // The rent of the last 18 months, one of them unpaid (the dashboard's arrears), the studio's yearly costs and the home's works.
  for (const [y, m] of months(2025, 5)) {
    if (y === 2026 && m === 8) continue;
    const on = date(y, m, 3);
    flows.push({ id: `fl-rent-${y}-${pad(m)}`, item: 'prop-studio', occurred_on: on, kind: 'income', category: 'rent', amount: eur(665) });
    flows.push({ id: `fl-charges-${y}-${pad(m)}`, item: 'prop-studio', occurred_on: on, kind: 'income', category: 'recoverable_charges', amount: eur(45) });
    if (m % 3 === 1) flows.push({ id: `fl-condo-${y}-${pad(m)}`, item: 'prop-studio', occurred_on: date(y, m, 10), kind: 'expense', category: 'condo_fees', amount: eur(285) });
    flows.push({ id: `fl-energy-${y}-${pad(m)}`, item: 'prop-home', occurred_on: date(y, m, 12), kind: 'expense', category: 'energy', amount: eur(145 + Math.round(40 * noise())) });
  }
  flows.push(
    { id: 'fl-home-tax-2025', item: 'prop-home', occurred_on: '2025-10-15', kind: 'expense', category: 'property_tax', amount: eur(1812) },
    { id: 'fl-studio-tax-2025', item: 'prop-studio', occurred_on: '2025-10-15', kind: 'expense', category: 'property_tax', amount: eur(704) },
    { id: 'fl-home-works-roof', item: 'prop-home', occurred_on: '2022-04-20', kind: 'expense', category: 'works', amount: eur(14200), note: 'Isolation des combles' },
    { id: 'fl-home-works-kitchen', item: 'prop-home', occurred_on: '2025-03-11', kind: 'expense', category: 'works', amount: eur(8650), note: 'Cuisine' },
    { id: 'fl-studio-maintenance', item: 'prop-studio', occurred_on: '2026-01-22', kind: 'expense', category: 'maintenance', amount: eur(380), note: 'Chauffe-eau' },
    { id: 'fl-studio-management', item: 'prop-studio', occurred_on: '2025-12-31', kind: 'expense', category: 'management_fees', amount: eur(540) },
  );
  const loans = [
    {
      id: 'loan-home', name: 'Prêt immobilier', lender: 'Banque Exemple', currency: 'EUR', principal: eur(265000), disbursed_on: '2021-08-25',
      first_payment_date: '2021-10-05', total_payments: 300, property: 'prop-home', ownership: halves(A, B), visibility: 'shared',
    },
    {
      id: 'loan-ptz', name: 'Prêt à taux zéro', lender: 'Banque Exemple', currency: 'EUR', principal: eur(40000), disbursed_on: '2021-08-25',
      first_payment_date: '2021-10-05', total_payments: 240, property: 'prop-home', ownership: halves(A, B), visibility: 'shared',
    },
    {
      id: 'loan-studio', name: 'Prêt du studio', lender: 'Crédit Exemple', currency: 'EUR', principal: eur(118000), disbursed_on: '2019-04-30',
      first_payment_date: '2019-06-10', total_payments: 240, property: 'prop-studio', ownership: mine(A),
    },
  ];
  const loan_phases = [
    { id: 'lp-home-1', loan: 'loan-home', seq: 1, mode: 'annuity', annual_rate: '0.0135', rate_type: 'fixed' },
    { id: 'lp-ptz-1', loan: 'loan-ptz', seq: 1, mode: 'deferred_total', annual_rate: '0', rate_type: 'fixed', payments: 120 },
    { id: 'lp-ptz-2', loan: 'loan-ptz', seq: 2, mode: 'annuity', annual_rate: '0', rate_type: 'fixed' },
    { id: 'lp-studio-1', loan: 'loan-studio', seq: 1, mode: 'annuity', annual_rate: '0.0115', rate_type: 'fixed' },
  ];
  const loan_insurances = [
    { id: 'li-home', loan: 'loan-home', mode: 'rate_on_initial', annual_rate: '0.0028', starts_on: '2021-10-05' },
    { id: 'li-studio', loan: 'loan-studio', mode: 'rate_on_initial', annual_rate: '0.0022', starts_on: '2019-06-10' },
  ];
  const loan_fees = [
    { id: 'fee-home-file', loan: 'loan-home', kind: 'file', amount: eur(950), on_date: '2021-08-25' },
    { id: 'fee-studio-file', loan: 'loan-studio', kind: 'file', amount: eur(600), on_date: '2019-04-30' },
  ];
  const loan_events = [
    { id: 'ev-studio-2024', loan: 'loan-studio', on_date: '2024-02-10', amount: eur(10000), effect: 'reduce_term', note: 'Prime placée sur le prêt' },
  ];

  // Income: Alice's salary (a raise, a yearly bonus, a one-off bonus) and the family allowance, shared by both adults.
  const income_sources = [
    { id: 'inc-alice-salary', name: 'Salaire Alice', kind: 'salary', payer_label: 'Entreprise Exemple', currency: 'EUR', ownership: mine(A), visibility: 'shared' },
    { id: 'inc-caf', name: 'Allocations familiales', kind: 'family_allowance', payer_label: 'CAF', currency: 'EUR', ownership: halves(A, B) },
  ];
  const income_phases = [
    { id: 'ip-alice-1', source: 'inc-alice-salary', label: 'Salaire de base', starts_on: '2024-01-01', ends_on: '2025-04-01', net_amount: eur(3120), every: every(1, 'month'), gross_amount: eur(4010), taxable_amount: eur(3290) },
    { id: 'ip-alice-2', source: 'inc-alice-salary', label: 'Salaire de base', starts_on: '2025-04-01', net_amount: eur(3285), every: every(1, 'month'), gross_amount: eur(4225), taxable_amount: eur(3465) },
    { id: 'ip-alice-bonus', source: 'inc-alice-salary', label: 'Prime annuelle', starts_on: '2024-12-20', net_amount: eur(2400), every: every(1, 'year') },
    { id: 'ip-caf', source: 'inc-caf', label: 'Allocations', starts_on: '2020-03-05', net_amount: eur(148.52), every: every(1, 'month') },
  ];
  flows.push({ id: 'fl-alice-oneoff', item: 'inc-alice-salary', occurred_on: '2026-06-30', kind: 'income', category: 'one_off', amount: eur(1500), note: 'Prime de projet' });
  const income_one_off_taxable = [{ flow_id: 'fl-alice-oneoff', taxable_amount: eur(1590) }];

  // Goals: a dated trip, the emergency fund, and a free goal followed by hand.
  const goals = [
    {
      id: 'goal-japan', name: 'Voyage au Japon', kind: 'savings', target_amount: '7000.00', target_date: '2027-09-01', note: 'Trois semaines, été 2027',
      ownership: halves(A, B), funding: [{ envelope: 'env-livret-a', share_bp: 4000 }, { envelope: 'env-cto', share_bp: 3000 }],
    },
    { id: 'goal-emergency', name: 'Épargne de précaution', kind: 'emergency_fund', months: 6, ownership: halves(A, B), funding: [{ envelope: 'env-livret-a', share_bp: 6000 }, { envelope: 'env-joint', share_bp: 5000 }] },
    {
      id: 'goal-bike', name: 'Vélo cargo', kind: 'savings', target_amount: '4200.00', ownership: mine(A),
      entries: [{ on: '2026-03-01', amount: '600.00' }, { on: '2026-06-01', amount: '1350.00' }, { on: '2026-09-01', amount: '2100.00' }],
    },
  ];

  // The home insurance in detail, and the region and sector split of the world ETF, at two dates.
  const subscription_insurances = [
    {
      id: 'ins-home', subscription: 'sub-home-insurance', kind: 'home', property: 'prop-home', insured_label: 'Maison de Nantes',
      policy_number: 'MRH-2021-0457', insurer_contact: '02 40 00 00 00',
      guarantees: [
        { label: 'Incendie et événements climatiques' }, { label: 'Dégât des eaux', deductible: '150 €' },
        { label: 'Vol et vandalisme', ceiling: '15 000 €', deductible: '300 €' }, { label: 'Responsabilité civile' },
      ],
    },
  ];
  const security_breakdowns = [
    {
      isin: 'IE00B4L5Y983', as_of: '2025-12-31',
      regions: [{ code: 'north_america', pct: '73.1' }, { code: 'europe_ex_france', pct: '11.4' }, { code: 'france', pct: '2.6' }, { code: 'japan', pct: '5.4' }, { code: 'asia_pacific_ex_japan', pct: '3.2' }, { code: 'other', pct: '4.3' }],
    },
    {
      isin: 'IE00B4L5Y983', as_of: '2026-09-30',
      regions: [{ code: 'north_america', pct: '74.2' }, { code: 'europe_ex_france', pct: '10.9' }, { code: 'france', pct: '2.5' }, { code: 'japan', pct: '5.1' }, { code: 'asia_pacific_ex_japan', pct: '3.1' }, { code: 'other', pct: '4.2' }],
      sectors: [
        { code: 'information_technology', pct: '26.8' }, { code: 'financials', pct: '16.2' }, { code: 'health_care', pct: '10.1' }, { code: 'industrials', pct: '10.9' },
        { code: 'consumer_discretionary', pct: '10.3' }, { code: 'communication_services', pct: '8.4' }, { code: 'consumer_staples', pct: '5.6' },
        { code: 'energy', pct: '3.7' }, { code: 'materials', pct: '3.2' }, { code: 'utilities', pct: '2.6' }, { code: 'real_estate', pct: '2.2' },
      ],
    },
  ];

  // Léa at a childminder since September 2025, four days a week; load.mjs validates her months through the API, whose figures
  // the server computes (a file's figures must match them to the cent).
  const childcare_arrangements = [
    {
      id: 'cc-lea', name: 'Assistante maternelle de Léa', child: 'dependent-1', carer: { name: 'Sophie Martin', approval: 'AM-44-2019-118' },
      terms: [{
        from: '2025-09-01', net_hourly_rate: '4.20', gross_hourly_rate: '5.40', overtime_pct: '25', upkeep_per_9h: '3.80', meal_price: '4.00',
        week_types: [{
          name: 'Semaine type', weeks: 45,
          schedule: [1, 2, 4, 5].map((day) => ({ day, arrival: '08:30', departure: '17:30', meal: true })),
        }],
      }],
      ownership: halves(A, B),
    },
  ];

  // The bank: the joint account (its envelope) and Alice's own, a year of lines. The last month is not reconciled yet: its lines
  // wait for a link, as after a sync.
  const bank_accounts = [
    { ref: 'acc-joint', name: 'Compte joint', iban_last4: '4821', kind: 'checking', currency: 'EUR', envelope_ref: 'env-joint', owner_ref: A },
    { ref: 'acc-alice', name: 'Compte courant Alice', iban_last4: '7310', kind: 'checking', currency: 'EUR', owner_ref: A },
  ];
  const bank_transactions = [];
  const bank_links = [];
  const monthly = (principal, rate, count) => (principal * (rate / 12)) / (1 - (1 + rate / 12) ** -count);
  const homeLoan = Math.round((monthly(265000, 0.0135, 300) + (265000 * 0.0028) / 12) * 100) / 100;
  const studioLoan = Math.round((monthly(118000, 0.0115, 240) + (118000 * 0.0022) / 12) * 100) / 100;
  const line = (account, ref, booked_on, amount, label, link) => {
    bank_transactions.push({ account_ref: account, provider_ref: ref, fingerprint: ref, booked_on, amount: amount.toFixed(2), currency: 'EUR', label, fetched_via: 'file' });
    if (link) bank_links.push({ account_ref: account, provider_ref: ref, ...link, decided_at: `${booked_on}T19:00:00Z` });
  };
  const groceries = ['CB CARREFOUR NANTES', 'CB BIOCOOP', 'CB MARCHE TALENSAC', 'CB LIDL REZE'];
  for (const [y, m] of months(2025, 11)) {
    const ym = `${y}-${pad(m)}`;
    const done = ym < LAST.slice(0, 7); // the last month waits for its links
    const to = (target_ref, outcome = 'as_expected') => (done ? { outcome, target_ref } : undefined);
    line('acc-alice', `alice-salary-${ym}`, date(y, m, 27), 3285, 'VIR SEPA ENTREPRISE EXEMPLE SALAIRE');
    line('acc-alice', `alice-to-joint-${ym}`, date(y, m, 1), -1800, 'VIR VERS COMPTE JOINT', done ? { outcome: 'internal_transfer', counterpart_ref: 'acc-joint' } : undefined);
    line('acc-joint', `joint-from-alice-${ym}`, date(y, m, 1), 1800, 'VIR DE ALICE DEV', done ? { outcome: 'internal_transfer', counterpart_ref: 'acc-alice' } : undefined);
    line('acc-joint', `joint-from-bob-${ym}`, date(y, m, 2), 1500, 'VIR DE BOB DEV');
    if (!(y === 2026 && m === 8)) line('acc-alice', `alice-rent-${ym}`, date(y, m, 3), 710, 'VIR LOCATAIRE STUDIO LYON', to('prop-studio', 'income'));
    line('acc-alice', `alice-loan-${ym}`, date(y, m, 10), -studioLoan, 'PRLV CREDIT EXEMPLE ECHEANCE PRET', to('loan-studio'));
    line('acc-alice', `alice-mobile-${ym}`, date(y, m, 4), -9.99, 'PRLV FORFAIT MOBILE', to('sub-mobile-alice'));
    line('acc-alice', `alice-icloud-${ym}`, date(y, m, 15), -2.99, 'APPLE.COM/BILL ICLOUD', to('sub-icloud'));
    line('acc-joint', `joint-loan-${ym}`, date(y, m, 5), -homeLoan, 'PRLV BANQUE EXEMPLE ECHEANCE PRET IMMO', to('loan-home'));
    line('acc-joint', `joint-spotify-${ym}`, date(y, m, 12), -17.99, 'PRLV SPOTIFY P2A4F81C', to('sub-spotify'));
    line('acc-joint', `joint-netflix-${ym}`, date(y, m, 5), -14.99, 'CB NETFLIX.COM', to('sub-netflix'));
    line('acc-joint', `joint-box-${ym}`, date(y, m, 20), -39.99, 'PRLV FIBRE MAISON FACTURE', to('sub-box'));
    line('acc-joint', `joint-caf-${ym}`, date(y, m, 5), 148.52, 'VIR CAF LOIRE ATLANTIQUE PRESTATIONS');
    line('acc-joint', `joint-energy-${ym}`, date(y, m, 12), -(145 + Math.round(40 * noise())), 'PRLV ENERGIE EXEMPLE');
    if (ym >= '2025-10') line('acc-joint', `joint-childcare-${ym}`, date(y, m, 6), -(560 + Math.round(30 * noise())), 'PRLV PAJEMPLOI URSSAF');
    groceries.forEach((label, i) => line('acc-joint', `joint-shop-${ym}-${i}`, date(y, m, 6 + i * 6), -(60 + Math.round(70 * Math.abs(noise())) + 0.37 * i), label));
    if (m === 9) line('acc-joint', `joint-insurance-${y}`, date(y, m, 1), -312.4, 'PRLV ASSUREUR EXEMPLE MRH', to('sub-home-insurance'));
  }
  const bank_rules = [
    { account_ref: 'acc-joint', label_stem: 'PRLV SPOTIFY', target_ref: 'sub-spotify', created_at: '2025-12-12T19:00:00Z' },
    { account_ref: 'acc-joint', label_stem: 'PRLV BANQUE EXEMPLE ECHEANCE PRET IMMO', target_ref: 'loan-home', created_at: '2025-12-05T19:00:00Z' },
  ];

  // Edge cases: an envelope that lost money over the year, and a parking space left vacant since its tenant left in June.
  envelopes.push({ id: 'env-tech', name: 'Compte-titres actions tech', kind: 'brokerage', institution: 'Courtier en ligne', opened_on: '2024-10-15', currency: 'EUR', ownership: mine(A) });
  valuations.push(...series('env-tech', [2024, 11], 6000, { rate: -0.012, jitter: 0.04 }));
  flows.push({ id: 'fl-tech-open', item: 'env-tech', occurred_on: '2024-10-15', kind: 'opening', amount: eur(6000) });
  properties.push({
    id: 'prop-parking', name: 'Parking Nantes centre', property_type: 'parking', currency: 'EUR', purchase_date: '2020-03-12', purchase_price: eur(19000),
    notary_fees: eur(2100), location_label: 'Nantes', rental_starts_on: '2020-04-01', ownership: mine(A),
  });
  valuations.push({ id: 'val-parking-2025', item: 'prop-parking', as_of: '2025-12-31', amount: eur(21000) });
  tenancies.push({ id: 'ten-parking-1', property: 'prop-parking', label: 'Place n° 12', starts_on: '2020-04-01', ends_on: '2026-06-30', rent_monthly: eur(85), deposit: eur(85) });

  return {
    persons, providers, subscriptions, billing_phases, purchases, envelopes, valuations, flows, holdings, holding_snapshots, holding_movements,
    properties, property_recurring_costs, tenancies, loans, loan_phases, loan_insurances, loan_fees, loan_events,
    subscription_insurances, income_sources, income_phases, income_one_off_taxable, security_breakdowns, goals,
    childcare_arrangements, bank_accounts, bank_transactions, bank_links, bank_rules,
  };
}

function bob() {
  const B = 'adult-1';
  const persons = [{ ref: B, kind: 'adult', display_name: 'Bob Dev', email: 'bob@example.test' }];
  const providers = [
    { key: 'demo-gamepass', name: 'Xbox Game Pass', website: 'https://www.xbox.com', label_keys: ['other'] },
    { key: 'demo-mobile-bob', name: 'Forfait mobile', label_keys: ['other'] },
  ];
  const subscriptions = [
    { id: 'bob-sub-gamepass', provider_key: 'demo-gamepass', name: 'Game Pass Ultimate', ownership: [{ person: B, share_bp: 10000 }], service_users: [B], auto_renew: true },
    { id: 'bob-sub-mobile', provider_key: 'demo-mobile-bob', name: 'Forfait mobile 40 Go', ownership: [{ person: B, share_bp: 10000 }], visibility: 'private', service_users: [B], auto_renew: true },
  ];
  const billing_phases = [
    { id: 'bob-ph-gamepass', subscription_id: 'bob-sub-gamepass', starts_on: '2024-10-01', ends_on: null, price: eur(17.99), every: every(1, 'month'), payer: { type: 'person', person: B } },
    { id: 'bob-ph-mobile', subscription_id: 'bob-sub-mobile', starts_on: '2023-05-18', ends_on: null, price: eur(7.99), every: every(1, 'month'), payer: { type: 'person', person: B } },
  ];
  const envelopes = [
    { id: 'bob-env-ldds', name: 'LDDS', kind: 'savings', institution: 'Banque Exemple', opened_on: '2018-09-01', currency: 'EUR', ownership: [{ person: B, share_bp: 10000 }] },
    { id: 'bob-env-per', name: 'PER', kind: 'per', institution: 'Assureur Exemple', opened_on: '2023-01-10', currency: 'EUR', ownership: [{ person: B, share_bp: 10000 }], visibility: 'private' },
  ];
  const valuations = [
    ...series('bob-env-ldds', [2024, 11], 7800, { monthly: 80, rate: 0.0019 }),
    ...series('bob-env-per', [2024, 11], 9600, { monthly: 200, rate: 0.005, jitter: 0.03 }),
  ];
  const flows = [
    { id: 'bob-fl-ldds-open', item: 'bob-env-ldds', occurred_on: '2018-09-01', kind: 'opening', amount: eur(2000) },
    { id: 'bob-fl-per-open', item: 'bob-env-per', occurred_on: '2023-01-10', kind: 'opening', amount: eur(3000) },
  ];
  for (const [y, m] of months(2024, 11)) {
    flows.push({ id: `bob-fl-ldds-${y}-${pad(m)}`, item: 'bob-env-ldds', occurred_on: date(y, m, 5), kind: 'contribution', amount: eur(80) });
    flows.push({ id: `bob-fl-per-${y}-${pad(m)}`, item: 'bob-env-per', occurred_on: date(y, m, 20), kind: 'contribution', amount: eur(200) });
  }
  const income_sources = [
    { id: 'bob-inc-salary', name: 'Salaire Bob', kind: 'salary', payer_label: 'Collectivité Exemple', currency: 'EUR', ownership: [{ person: B, share_bp: 10000 }] },
    { id: 'bob-inc-freelance', name: 'Missions de traduction', kind: 'self_employment', currency: 'EUR', ownership: [{ person: B, share_bp: 10000 }] },
  ];
  const income_phases = [
    { id: 'bob-ip-salary-1', source: 'bob-inc-salary', label: 'Traitement', starts_on: '2024-01-01', ends_on: '2026-01-01', net_amount: eur(2480), every: every(1, 'month') },
    { id: 'bob-ip-salary-2', source: 'bob-inc-salary', label: 'Traitement', starts_on: '2026-01-01', net_amount: eur(2560), every: every(1, 'month') },
    { id: 'bob-ip-freelance', source: 'bob-inc-freelance', label: 'Missions', starts_on: '2025-01-01', ends_on: '2026-12-31', net_amount: eur(900), every: every(3, 'month') },
  ];
  return { persons, providers, subscriptions, billing_phases, envelopes, valuations, flows, income_sources, income_phases };
}

/**
 * Moves every date of a file by `months` whole months: a month's last day stays its last day, a 31st becomes the 30th of a
 * shorter month. Times keep their part after the date. Ids keep the month they were written with: only their uniqueness counts.
 */
export function shiftDates(value, months) {
  if (months === 0) return value;
  if (Array.isArray(value)) return value.map((v) => shiftDates(v, months));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, shiftDates(v, months)]));
  const found = typeof value === 'string' && /^(\d{4})-(\d{2})-(\d{2})(T.*)?$/.exec(value);
  if (!found) return value;
  const [y, m, d] = [Number(found[1]), Number(found[2]), Number(found[3])];
  const days = (year, month) => new Date(Date.UTC(year, month, 0)).getUTCDate();
  const at = y * 12 + (m - 1) + months;
  const [ny, nm] = [Math.floor(at / 12), (at % 12) + 1];
  const day = d === days(y, m) ? days(ny, nm) : Math.min(d, days(ny, nm));
  return `${ny}-${pad(nm)}-${pad(day)}${found[4] ?? ''}`;
}

/** Both files, their last facts in the month `anchor` (`YYYY-MM`); the checked-in files end in LAST's month. */
export function household(anchor = LAST.slice(0, 7)) {
  seed = 42;
  const [ay, am] = anchor.split('-').map(Number);
  const [ly, lm] = LAST.split('-').map(Number);
  const months = ay * 12 + am - (ly * 12 + lm);
  const file = (sections) => shiftDates({ format: 'nid.data', version: 1, ...sections }, months);
  return { alice: file(alice()), bob: file(bob()) };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  for (const [name, file] of Object.entries(household())) writeFileSync(join(here, `${name}.nid.json`), JSON.stringify(file, null, 2) + '\n');
}
