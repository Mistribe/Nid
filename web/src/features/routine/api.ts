import type { components } from '@/api/schema';

export type RoutineView = components['schemas']['RoutineView'];
export type RoutineLine = components['schemas']['RoutineLineView'];
export type RoutineRecap = components['schemas']['RoutineRecap'];
export type RoutineRecapLine = components['schemas']['RoutineRecapLine'];

export const ROUTINE = '/api/v1/investments/routine' as const;
export const ROUTINE_UNDO = '/api/v1/investments/routine/undo' as const;

/** Spec 3E2: the properties' routine, its own reading, recording and undo. */
export type EstateRoutineView = components['schemas']['PropertyRoutineView'];
export type EstateEstimate = components['schemas']['PropertyRoutineEstimateView'];
export type EstateCost = components['schemas']['PropertyRoutineCostView'];
export type EstateRent = components['schemas']['PropertyRoutineRentView'];
export type EstateRecap = components['schemas']['PropertyRoutineRecap'];
export type EstateRecapLine = components['schemas']['PropertyRoutineRecapLine'];

export const ESTATE_ROUTINE = '/api/v1/realestate/routine' as const;
export const ESTATE_ROUTINE_UNDO = '/api/v1/realestate/routine/undo' as const;
