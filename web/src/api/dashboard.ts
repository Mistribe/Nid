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

export type MonthChanges = components['schemas']['MonthChangesView'];
export type MonthFigure = components['schemas']['MonthFigureView'];

/** Issue #415: « Depuis le mois dernier ». */
export const MONTH_CHANGES = '/api/v1/dashboard/changes' as const;

export type Projection = components['schemas']['ProjectionView'];
export type HouseholdPoint = components['schemas']['HouseholdPointView'];
export type ProjectionMilestone = components['schemas']['ProjectionMilestoneView'];
/** Ranges spec §4: the bands of the projection, asked with `ranges=true`. */
export type ProjectionRanges = components['schemas']['ProjectionRangesView'];
export type RangePoint = components['schemas']['RangePointView'];

/** The household's net worth projection (issue 102); the settings page invalidates it too. */
export const PROJECTION = '/api/v1/projection' as const;
