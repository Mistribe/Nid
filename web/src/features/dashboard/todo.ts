import type { DashboardTodo } from './api';

/** Stale and missing values are the month's routine; an income gap is a different job (« Renseigner la suite »). */
export const isRoutineTodo = (row: DashboardTodo) => row.kind === 'stale_value' || row.kind === 'no_value';
export const incomeGaps = (todo: readonly DashboardTodo[]) => todo.filter((row) => row.kind === 'income_gap_soon');
