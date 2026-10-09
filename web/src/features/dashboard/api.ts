import type { components } from '@/api/schema';

export type DashboardView = components['schemas']['DashboardView'];
export type DashboardFigure = components['schemas']['DashboardFigure'];

export const DASHBOARD = '/api/v1/dashboard' as const;

export type DashboardEnvelope = components['schemas']['DashboardEnvelopeView'];
export type DashboardPerson = components['schemas']['DashboardPersonView'];
export type DashboardEvent = components['schemas']['DashboardEventView'];
export type DashboardTodo = components['schemas']['DashboardTodoView'];
