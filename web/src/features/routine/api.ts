import type { components } from '@/api/schema';

export type RoutineView = components['schemas']['RoutineView'];
export type RoutineLine = components['schemas']['RoutineLineView'];
export type RoutineRecap = components['schemas']['RoutineRecap'];
export type RoutineRecapLine = components['schemas']['RoutineRecapLine'];

export const ROUTINE = '/api/v1/investments/routine' as const;
export const ROUTINE_UNDO = '/api/v1/investments/routine/undo' as const;
