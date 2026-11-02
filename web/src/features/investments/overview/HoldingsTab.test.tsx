import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { HouseholdHoldings, HoldingsExposure } from '../api';
import { holdingRow, householdHoldings, LIFE, PEA, WORLD_PEA } from '../testData';

const serve = (body: HouseholdHoldings, seen: string[] = []) =>
  server.use(http.get('*/api/v1/investments/overview/holdings', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
const region = (name: string) => screen.findByRole('region', { name });

describe('Placements › supports et frais', () => {
  beforeAll(async () => { await import('../EnvelopesPage'); await import('./HoldingsTab'); }, 30_000);

  it('is absent while no envelope is detailed', async () => {
    await renderApp('/investments?tab=holdings');

    expect(await screen.findByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByRole('tab', { name: 'Supports et frais' })).not.toBeInTheDocument();
  });

  it('shows the server figures and the holding held twice', async () => {
    const seen: string[] = [];
    serve(householdHoldings(), seen);
    const { container } = await renderApp('/investments?tab=holdings');

    const figures = (await screen.findByText('Détaillé par support')).closest('dl')!;
    expect(figures).toHaveTextContent('139 700,00 €2 enveloppes sur 3 · 85,8 % de la valeur');
    expect(figures).toHaveTextContent('Part en unités de compte64,0 %assurance-vie et PER · plancher 60,0 %');
    expect(figures).toHaveTextContent('Frais estimés en 2024≈ 233,75 €≈ 0,9 % des encours · estimation');
    expect(figures).toHaveTextContent('Prochaine échéance5 déc. 2025Athena 2023');
    expect(screen.getByRole('note')).toHaveTextContent('Amundi MSCI World pèse 43,2 % du foyer, détenu dans 2 enveloppes.');
    expect(seen[0]).toBe('?scope=household');
    await expectNoAxeViolations(container);
  });

  it('says « estimé » next to a total that counts an estimate', async () => {
    serve(householdHoldings({ estimated: true }));
    const { container } = await renderApp('/investments?tab=holdings');

    expect((await screen.findByText('Détaillé par support')).closest('dl')).toHaveTextContent('139 700,00 € estimé2 enveloppes sur 3');
    await expectNoAxeViolations(container);
  });

  it('splits by kind, says what is outside, and lists the holdings to their page', async () => {
    serve(householdHoldings());
    const { user } = await renderApp('/investments?tab=holdings');

    const kinds = await region('Exposition par type de support');
    expect(within(kinds).getAllByRole('row')[0]).toHaveTextContent('ETF79 900,00 €57,2 %');
    expect(kinds).toHaveTextContent('Hors détail : Livret A 22 950,00 €, non réparti ou évolution depuis le relevé 200,00 €.');

    const table = await screen.findByRole('table', { name: 'Supports des enveloppes' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Support', 'Enveloppe', 'Valeur', 'Poids', 'Gain %', 'Rendement annuel', 'Frais totaux']);
    const [first] = within(table).getAllByRole('row').slice(1);
    expect(within(first!).getByRole('link', { name: /Amundi MSCI World/ })).toHaveAttribute('href', `/investments/${PEA}/holdings/${WORLD_PEA}`);
    await user.click(screen.getByRole('radio', { name: 'Par enveloppe' }));
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('Assurance-vie');
  });

  it('says the fees are an estimate and what to watch', async () => {
    serve(householdHoldings());
    await renderApp('/investments?tab=holdings');

    const fees = await region('Ce que coûtent les frais (2024)');
    expect(fees).toHaveTextContent('iShares MSCI World 0,9 %≈ 233,75 €');
    expect(fees).toHaveTextContent('Toujours une estimation.');
    expect(fees).toHaveTextContent('2 supports n’ont pas de rapport de frais pour 2024.');
    const watch = within(await region('À surveiller')).getAllByRole('listitem');
    expect(watch[0]).toHaveTextContent('PEAParts inexpliquées sur Amundi MSCI World au relevé du 30 sept. 2025');
    expect(within(watch[1]!).getByRole('link', { name: 'Assurance-vie' })).toHaveAttribute('href', expect.stringContaining(`/investments/${LIFE}/holdings/`));
  });

  it('shows each envelope’s fees and what they cost against the household’s references, which it saves', async () => {
    const eur = (amount: string) => ({ amount, currency: 'EUR' });
    const cost = (name: string, feesPct: number, referencePct: number, gap20: string, holdingId: string | null = null) => ({
      holdingId, name, value: eur('28200.00'), feesPct, referencePct, yearly: eur('169.20'), cost10: eur('2346.67'), cost20: eur('6752.02'), gap20: eur(gap20),
    });
    serve(householdHoldings({
      envelopeFees: [
        { envelopeId: LIFE, name: 'Assurance-vie', contractFeesPct: 0.6, supportFeesPct: 0, totalFeesPct: 0.73, yearlyCost: eur('436.10'), coveredPct: 76 },
        { envelopeId: PEA, name: 'PEA', contractFeesPct: null, supportFeesPct: null, totalFeesPct: null, yearlyCost: null, coveredPct: 0 },
      ],
      projection: {
        assumedReturnPct: 4, yearly: eur('436.10'), cost10: eur('6008.78'), cost20: eur('17166.54'), aboveReference20: eur('2796.18'),
        envelopes: [{ envelopeId: LIFE, total: cost('Assurance-vie', 0.73, 0.61, '2796.18'), holdings: [cost('Fonds euros', 0.6, 0.5, '1074.40', '0192f0aa-0000-7000-8000-00000000e001')] }],
      },
    }));
    let sent: Record<string, unknown> = {};
    server.use(http.put('*/api/v1/investments/fee-references', async ({ request }) => {
      sent = await request.json() as Record<string, unknown>;
      return HttpResponse.json(sent);
    }));
    const { container, user } = await renderApp('/investments?tab=holdings');

    const table = await screen.findByRole('table', { name: 'Frais de chaque enveloppe' });
    const rows = within(table).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('Assurance-vie0,6 %0 %0,73 %≈ 436,10 €76,0 %');
    expect(within(rows[1]!).getByRole('link', { name: 'Assurance-vie' })).toHaveAttribute('href', `/investments/${LIFE}?tab=holdings`);
    expect(rows[2]).not.toHaveTextContent('0,00 €');

    const projection = await region('Ce que coûtent les frais dans le temps');
    expect(projection).toHaveTextContent('Au-dessus de la référence sur 20 ans2 796,18 €');
    const lines = within(within(projection).getByRole('table')).getAllByRole('row');
    expect(lines[1]).toHaveTextContent('Assurance-vie');
    expect(lines[2]).toHaveTextContent('Fonds euros0,6 % · référence 0,5 %0,6 %0,5 %169,20 €2 346,67 €6 752,02 €1 074,40 €');
    expect(projection).toHaveTextContent('Indicatif');
    expect(projection).toHaveTextContent('La référence est votre choix, pas un conseil');
    await expectNoAxeViolations(container);

    await user.click(within(projection).getByRole('button', { name: 'Références…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Frais de référence' });
    const etf = within(dialog).getByRole('textbox', { name: 'ETF' });
    expect(etf).toHaveValue('0,2');
    await user.clear(etf);
    await user.type(etf, '12');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    expect(within(dialog).getByRole('alert')).toHaveTextContent('Rendement de -10 à 20 %, chaque frais de 0 à 10 %.');
    await user.clear(etf);
    await user.type(etf, '0,1');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));
    expect(await within(projection).findByRole('status')).toHaveTextContent('Références enregistrées.');
    expect(sent).toMatchObject({ assumedReturnPct: 4, envelopes: { life_insurance: 0.5 }, holdings: { etf: 0.1, euro_fund: 0 } });
  });

  it('says why last year is not estimated when reports exist, and names a holding without one once', async () => {
    const euro = (n: number) => holdingRow({ holdingId: `0192f0aa-0000-7000-8000-00000000d00${n}`, name: 'Fonds euros', kind: 'euro_fund', totalFeesPct: null, implicitCost: null });
    serve(householdHoldings({
      holdings: [holdingRow({ totalFeesPct: 2.5, implicitCost: null }), euro(1), euro(2)], cost: null, costPct: null, withoutReportCount: 2,
    }));
    await renderApp('/investments?tab=holdings');

    const fees = await region('Ce que coûtent les frais (2024)');
    expect(fees).toHaveTextContent('Coût de 2024 non estimé : il faut la valeur du support au relevé de fin 2023 et à celui de fin 2024.');
    expect(fees).not.toHaveTextContent('Aucun rapport de frais');
    expect(fees).toHaveTextContent('Sans rapport de frais pour 2024 : Fonds euros (2).');
  });

  describe('exposure', () => {
    const REGIONS = ['north_america', 'europe_ex_france', 'france', 'japan', 'asia_pacific_ex_japan', 'emerging', 'other'];
    const SECTORS = ['information_technology', 'health_care', 'financials', 'industrials', 'consumer_discretionary', 'consumer_staples', 'energy',
      'materials', 'communication_services', 'utilities', 'real_estate', 'other'];
    const rest = (amount: string, sharePct: number) => ({ value: { amount, currency: 'EUR' }, sharePct });
    const spread = (codes: string[], some: Record<string, [string, number]>) =>
      codes.map((code) => ({ code, ...rest(...(some[code] ?? ['0.00', 0])) }));
    const exposure = (over: Partial<HoldingsExposure> = {}): HoldingsExposure => ({
      regions: spread(REGIONS, { north_america: ['60000.00', 36.8], europe_ex_france: ['20000.00', 12.3] }),
      sectors: spread(SECTORS, { information_technology: ['50000.00', 30.7], health_care: ['10000.00', 6.1] }),
      outsideMarkets: rest('59800.00', 36.7), unknown: rest('23050.00', 14.2), unclassifiedSectors: rest('20000.00', 12.3), ...over,
    });

    it('shows the exposure by region and by sector from the server, zeros hidden', async () => {
      serve(householdHoldings({ exposure: exposure() }));
      const { container } = await renderApp('/investments?tab=holdings');

      const section = await region('Exposition par région et par secteur');
      const regions = within(within(section).getByRole('table', { name: 'Exposition par région' }));
      const regionsRows = regions.getAllByRole('row');
      [
        'Amérique du Nord60 000,00 €36,8 %', 'Europe hors France20 000,00 €12,3 %', 'Hors marchés59 800,00 €36,7 %', 'Inconnu23 050,00 €14,2 %',
      ].forEach((text, i) => expect(regionsRows[i]).toHaveTextContent(text));
      expect(regionsRows).toHaveLength(4);
      const sectors = within(within(section).getByRole('table', { name: 'Exposition par secteur' }));
      const sectorsRows = sectors.getAllByRole('row');
      [
        'Technologies de l’information50 000,00 €30,7 %', 'Santé10 000,00 €6,1 %', 'Hors marchés59 800,00 €36,7 %', 'Inconnu23 050,00 €14,2 %', 'Non classé20 000,00 €12,3 %',
      ].forEach((text, i) => expect(sectorsRows[i]).toHaveTextContent(text));
      expect(sectorsRows).toHaveLength(5);
      await expectNoAxeViolations(container);
    });

    it('names the old factsheets', async () => {
      serve(householdHoldings({ exposure: exposure({ staleValue: { amount: '4000.00', currency: 'EUR' } }) }));
      await renderApp('/investments?tab=holdings');

      expect(await region('Exposition par région et par secteur')).toHaveTextContent('dont 4 000,00 € sur des fiches de plus d’un an');
    });

    it('explains how to fill a breakdown when all is unknown', async () => {
      serve(householdHoldings({ exposure: exposure({
        regions: spread(REGIONS, {}), sectors: spread(SECTORS, {}), outsideMarkets: rest('0.00', 0), unknown: rest('139700.00', 100), unclassifiedSectors: rest('0.00', 0),
      }) }));
      await renderApp('/investments?tab=holdings');

      expect(await region('Exposition par région et par secteur')).toHaveTextContent('Aucune répartition n’est renseignée : ouvrez la fiche d’un support et choisissez « Renseigner » dans « Répartition ».');
    });

    it('shows nothing when the exposure is null and says a rate is missing', async () => {
      serve(householdHoldings({
        exposure: null,
        findings: [{ code: 'core.fx_rate_missing', reference: 'USD:EUR', message: 'x' }],
      }));
      await renderApp('/investments?tab=holdings');

      const section = await region('Exposition par région et par secteur');
      expect(section).toHaveTextContent('Taux de change manquant : USD');
      expect(within(section).queryByRole('table')).not.toBeInTheDocument();
    });
  });
});
