// What the import format has no section for, seeded through the API as Alice: tax notices, scenarios, projection scenarios,
// documents and the CPI series. Each step looks for what it would create first, so a second run changes nothing.
import { shiftDates } from './generate.mjs';

/** The first of the month `months` after `anchor` (`YYYY-MM`), as `YYYY-MM-DD`. */
const monthAfter = (anchor, months) => {
  const [y, m] = anchor.split('-').map(Number);
  const at = new Date(Date.UTC(y, m - 1 + months, 1));
  return at.toISOString().slice(0, 10);
};
const eur = (amount) => ({ amount: String(amount), currency: 'EUR' });

/** A one-page PDF holding `lines` of text: enough for the documents screens, and the same bytes for the same lines. */
export function pdf(lines) {
  const text = lines.map((line, i) => `BT /F1 ${i === 0 ? 16 : 11} Tf 60 ${770 - i * 22} Td (${line.replace(/[()\\]/g, '\\$&')}) Tj ET`).join('\n');
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>',
    `<< /Length ${Buffer.byteLength(text, 'latin1')} >>\nstream\n${text}\nendstream`,
  ];
  let body = '%PDF-1.4\n';
  const offsets = objects.map((object, i) => {
    const at = Buffer.byteLength(body, 'latin1');
    body += `${i + 1} 0 obj\n${object}\nendobj\n`;
    return at;
  });
  const xref = Buffer.byteLength(body, 'latin1');
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}`;
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(body, 'latin1');
}

/**
 * Every month of each arrangement up to `previous` (a Date, the first of last month), saved as the server plans it and
 * validated: the server computes the pay. A month already validated is left as it is.
 */
export async function validateChildcare({ call }, previous) {
  for (const arrangement of (await call('GET', '/api/v1/childcare')).items) {
    let validated = 0;
    for (let at = new Date(`${arrangement.start.slice(0, 7)}-01T00:00:00Z`); at <= previous; at.setUTCMonth(at.getUTCMonth() + 1)) {
      const path = `/api/v1/childcare/${arrangement.id}/statements/${at.toISOString().slice(0, 7)}`;
      const draft = await call('GET', path);
      if (draft.state === 'validated') continue;
      const days = draft.days.map(({ date, present, arrival, departure, lunch, snack, absence }) => ({ date, present, arrival, departure, lunch, snack, absence }));
      const saved = await call('PUT', path, { days, mileage: '0', cmg: draft.cmgEstimate ?? '0', version: draft.version });
      await call('POST', `${path}/validate`, { version: saved.version });
      validated++;
    }
    console.log(`${arrangement.name}: ${validated} months validated.`);
  }
}

export async function seed({ call }, anchor, shift) {
  const persons = await call('GET', '/api/v1/core/persons');
  const alice = persons.find((p) => p.email === 'alice@example.test').id;
  const bob = persons.find((p) => p.email === 'bob@example.test').id;
  const byName = async (path, field = 'name') => Object.fromEntries((await call('GET', path)).items.map((i) => [i[field], i.id]));
  const properties = await byName('/api/v1/realestate/properties?scope=household');
  const loans = await byName('/api/v1/realestate/loans?scope=household');
  const incomes = await byName('/api/v1/income/sources?scope=household', 'displayName');
  const envelopes = await byName('/api/v1/investments/envelopes?scope=household', 'displayName');
  const halves = [{ personId: alice, shareBp: 5000 }, { personId: bob, shareBp: 5000 }];
  // The latest notice out: the one on last year's income from September, when it arrives, else the year before.
  const latest = new Date().getFullYear() - (new Date().getMonth() >= 8 ? 1 : 2);
  const anchorYear = Number(anchor.slice(0, 4));
  const done = [];

  // Documents: each linked to what it proves. Left without one on purpose, as « documents attendus »: the studio's loan
  // amortization table, the studio's and the parking's deeds.
  const upload = async (title, date, lines, link) => {
    const multipart = { file: { name: `${title}.pdf`, mimeType: 'application/pdf', buffer: pdf([title, '', ...lines, '', 'Document fictif : foyer de démonstration de Nid.']) }, title, date, warnDuplicate: 'true', ...link };
    const answer = await call('POST', '/api/v1/documents', undefined, { multipart, allow: [409] });
    if (!answer.duplicate) done.push(title);
    return answer.id ?? answer.duplicate.id;
  };
  const day = (text) => shiftDates(text, shift);
  const item = (targetId, role) => ({ targetKind: 'core.item', targetId, role });
  await upload('Offre de prêt immobilier', day('2021-07-20'), ['Banque Exemple', 'Capital : 265 000 EUR sur 300 mois', 'Taux nominal fixe : 1,35 %'], item(loans['Prêt immobilier'], 'loan_offer'));
  await upload('Tableau d\'amortissement du prêt immobilier', day('2021-08-25'), ['Banque Exemple', '300 échéances à partir du 5 octobre 2021'], item(loans['Prêt immobilier'], 'amortization_table'));
  await upload('Acte de vente de la maison de Nantes', day('2021-09-01'), ['Étude notariale Exemple', 'Prix : 320 000 EUR'], item(properties['Maison de Nantes'], 'deed'));
  await upload('Bail du studio Lyon 7e', day('2023-07-15'), ['Location meublée, loyer 665 EUR + 45 EUR de charges', 'Révision annuelle selon l\'IRL du 2e trimestre'], item(properties['Studio Lyon 7e'], 'lease'));
  await upload(`Bulletin de salaire ${anchor}`, day('2026-10-28'), ['Entreprise Exemple', 'Net à payer : 3 285,00 EUR'], item(incomes['Salaire Alice'], 'payslip'));
  await upload(`Relevé annuel assurance-vie ${anchorYear - 1}`, `${anchorYear}-01-31`, ['Assureur Exemple', 'Fonds euros et ETF MSCI World'], item(envelopes['Assurance-vie'], 'statement'));
  const notice = await upload(`Avis d'impôt ${latest + 1} sur les revenus ${latest}`, `${latest + 1}-08-01`, ['Direction générale des finances publiques', 'Foyer : Alice Dev et Bob Dev, 2,5 parts']);

  // Tax notices: the household's last two, the latest with its PDF.
  const taxed = new Set((await call('GET', '/api/v1/tax/assessments?scope=household')).items.map((a) => a.year));
  for (const [revenue, raise, documentId] of [[latest, 1, notice], [latest - 1, 0.97, undefined]]) {
    if (taxed.has(revenue)) continue;
    const gross = (amount) => eur(Math.round(amount * raise));
    await call('POST', '/api/v1/tax/assessments', {
      year: revenue, documentId,
      declarants: [
        { personId: alice, rank: 1, grossSalaries: gross(50700), deduction: 'allowance', deductionAmount: gross(5070) },
        { personId: bob, rank: 2, grossSalaries: gross(39400), deduction: 'allowance', deductionAmount: gross(3940) },
      ],
      shares: '2.5', referenceIncome: gross(81090), averageRate: '6.1', marginalRate: '30',
      netTax: gross(4946), withholding: gross(4890), balance: gross(56), childcareExpenses: gross(3500),
    });
    done.push(`avis ${revenue}`);
  }

  // Scenarios: a rental purchase, the studio's sale, and two projections next to the base one.
  const scenarios = new Set((await call('GET', '/api/v1/scenarios?scope=household')).items.map((s) => s.name));
  const scenario = async (path, name, owners, document) => {
    if (scenarios.has(name)) return;
    await call('POST', path, { name, visibility: 'shared', scope: 'household', owners, document: { version: 1, ...document } });
    done.push(name);
  };
  await scenario('/api/v1/scenarios', 'Appartement locatif à Rennes', halves, {
    use: 'rental', purchaseOn: monthAfter(anchor, 6), price: '210000', notaryFees: '16000', agencyFees: '0', otherCosts: '0',
    monthlyCharges: '120', currentRent: '0', expectedRent: '850', vacancyMonths: 1, rentalRegime: 'real', funding: [],
    loans: [{ name: 'Banque Exemple', amount: '228700', rate: '3.4', months: 240, deferral: null, insurance: { kind: 'rate_on_initial', value: '0.3' }, fileFees: '800', guaranteeFees: '1900' }],
    assumptions: { horizonYears: 15, classRates: { cash: '2', equity: '6' }, propertyGrowth: '1.5', monthlySavings: '400', savingsClass: 'cash' },
  });
  await scenario('/api/v1/scenarios/sale', 'Vente du studio de Lyon', [{ personId: alice, shareBp: 10000 }], {
    property: properties['Studio Lyon 7e'], saleOn: monthAfter(anchor, 12), price: '158000', agencyFees: '6000', penalties: '1100', horizonYears: 10,
  });
  await scenario('/api/v1/scenarios/projection', 'Congé parental de Bob', halves, {
    horizon: '15y', assumptions: {},
    events: [{ kind: 'income_change', label: 'Congé parental', start: { on: monthAfter(anchor, 4) }, until: { on: monthAfter(anchor, 16) }, monthlyAmount: '-1700' }],
  });
  await scenario('/api/v1/scenarios/projection', 'Remboursement anticipé du prêt immobilier', halves, {
    horizon: '20y', assumptions: {},
    events: [{ kind: 'early_repayment', label: 'Prime et épargne', start: { on: monthAfter(anchor, 20) }, amount: '30000', split: 'chosen', loan: loans['Prêt immobilier'], effect: 'reduce_term', penalty: '600' }],
  });

  // The CPI: INSEE's series when the stack can reach it, else a fictional one, so the real figures show either way.
  if (!(await call('GET', '/api/v1/core/inflation')).lastPublished) {
    await call('PATCH', '/api/v1/admin/household', { inflationEnabled: true });
    // An instance without scheduled jobs (the public demo, Jobs__Enabled=false) refuses the sync: straight to the fictional series.
    const refused = (await call('POST', '/api/v1/core/inflation/sync', {}, { allow: [422] }))?.status === 422;
    for (let i = 0; !refused && i < 15 && !(await call('GET', '/api/v1/core/inflation')).lastPublished; i++) await new Promise((r) => setTimeout(r, 2000));
    if ((await call('GET', '/api/v1/core/inflation')).lastPublished) done.push('IPC (INSEE)');
    else {
      for (let i = 48, value = 112; i >= 1; i--, value *= 1.0017) {
        const [y, m] = monthAfter(anchor, -i + 1).split('-').map(Number);
        await call('PUT', `/api/v1/core/inflation/${y}/${m}`, { value: value.toFixed(2) });
      }
      done.push('IPC (série fictive, INSEE injoignable)');
    }
  }
  return done;
}
