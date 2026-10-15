import type {
  EstateCost, EstateEstimate, EstateRecap, EstateRecapLine, EstateRent, EstateRoutineView, RoutineLine, RoutineRecap, RoutineRecapLine, RoutineView,
} from './api';

const eur = (amount: string) => ({ amount, currency: 'EUR' });

export const PEA = '00000000-0000-7000-8000-0000000000f1';
export const LIVRET = '00000000-0000-7000-8000-0000000000f2';
export const MANUEL = '00000000-0000-7000-8000-0000000000f3';

export const line = (over: Partial<RoutineLine> = {}): RoutineLine => ({
  id: PEA, name: 'PEA', currency: 'EUR', cadence: 'monthly', lastValue: eur('1000.00'), lastAsOf: '2025-08-31', due: true, ...over,
});

export const routine = (lines: RoutineLine[], on = '2025-09-30'): RoutineView => ({ on, lines });

export const recapLine = (over: Partial<RoutineRecapLine> = {}): RoutineRecapLine => ({
  envelope: PEA, name: 'PEA', previousValue: eur('1000.00'), previousAsOf: '2025-08-31', value: eur('1100.00'),
  contributions: eur('50.00'), gain: eur('50.00'), valuationId: '00000000-0000-7000-8000-0000000000a9', flowId: null, ...over,
});

export const recap = (lines: RoutineRecapLine[], on = '2025-09-30'): RoutineRecap => ({ on, lines });

export const MAISON = '00000000-0000-7000-8000-0000000000e1';
export const LOCATIF = '00000000-0000-7000-8000-0000000000e2';
export const TAX = '00000000-0000-7000-8000-0000000000c1';
export const LEASE = '00000000-0000-7000-8000-0000000000d1';
export const VALUATION = '00000000-0000-7000-8000-0000000000b1';
export const FLOW = '00000000-0000-7000-8000-0000000000b2';

export const estimate = (over: Partial<EstateEstimate> = {}): EstateEstimate => ({
  property: MAISON, name: 'Maison', currency: 'EUR', lastValue: '290000.00', lastAsOf: '2024-06-30', due: true, ...over,
});

export const cost = (over: Partial<EstateCost> = {}): EstateCost => ({
  property: MAISON, name: 'Maison', currency: 'EUR', costId: TAX, category: 'property_tax', amount: '1200.00', dueOn: '2025-09-15',
  alreadyEntered: null, ...over,
});

export const rent = (over: Partial<EstateRent> = {}): EstateRent => ({
  property: LOCATIF, name: 'Locatif', currency: 'EUR', tenancyId: LEASE, label: 'T2', amount: '950.00', occurredOn: '2025-09-01',
  alreadyEntered: null, ...over,
});

export const estateRoutine = (over: Partial<EstateRoutineView> = {}): EstateRoutineView => ({
  on: '2025-09-30', estimates: [], costs: [], rents: [], ...over,
});

export const estateRecapLine = (over: Partial<EstateRecapLine> = {}): EstateRecapLine => ({
  property: MAISON, name: 'Maison', currency: 'EUR', valuationId: VALUATION, estimate: '300000.00', flows: [], ...over,
});

export const estateRecap = (lines: EstateRecapLine[], on = '2025-09-30'): EstateRecap => ({ on, lines });
