import type { RoutineLine, RoutineRecap, RoutineRecapLine, RoutineView } from './api';

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
