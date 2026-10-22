import type { components } from '@/api/schema';

export type DashboardView = components['schemas']['DashboardView'];
export type DashboardFigure = components['schemas']['DashboardFigure'];
export type DashboardCommitmentSource = components['schemas']['DashboardCommitmentSourceView'];

export const DASHBOARD = '/api/v1/dashboard' as const;

export type DashboardEnvelope = components['schemas']['DashboardEnvelopeView'];
export type DashboardPerson = components['schemas']['DashboardPersonView'];
export type DashboardEvent = components['schemas']['DashboardEventView'];
export type DashboardTodo = components['schemas']['DashboardTodoView'];
export type Allocation = components['schemas']['AllocationView'];
export type AllocationLine = components['schemas']['AllocationLineView'];

export const ALLOCATION_TARGET = '/api/v1/dashboard/allocation-target' as const;

export type NetWorthHistory = components['schemas']['NetWorthHistoryView'];
export type NetWorthPointView = components['schemas']['NetWorthPointView'];

export const NET_WORTH = '/api/v1/dashboard/net-worth' as const;
