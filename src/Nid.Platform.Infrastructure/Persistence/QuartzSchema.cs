namespace Nid.Platform.Infrastructure.Persistence;

/// <summary>
/// The tables of Quartz.NET 4.1.1 (<c>database/tables/tables_postgres.sql</c> at tag v4.1.1), in the platform
/// schema, brought to 4.6.2 by <see cref="Upgrade46"/>. Created by migrations so the schema has one owner (EF);
/// Quartz only validates it at startup. A Quartz upgrade that changes the schema needs a new migration with the
/// upstream migration scripts.
/// </summary>
public static class QuartzSchema
{
    public const string Create = """
        CREATE TABLE platform.qrtz_job_details (
            sched_name TEXT NOT NULL, job_name TEXT NOT NULL, job_group TEXT NOT NULL, description TEXT NULL,
            job_class_name TEXT NOT NULL, is_durable BOOL NOT NULL, is_nonconcurrent BOOL NOT NULL,
            is_update_data BOOL NOT NULL, requests_recovery BOOL NOT NULL, job_data BYTEA NULL,
            PRIMARY KEY (sched_name, job_name, job_group));

        CREATE TABLE platform.qrtz_triggers (
            sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL,
            job_name TEXT NOT NULL, job_group TEXT NOT NULL, description TEXT NULL,
            next_fire_time BIGINT NULL, prev_fire_time BIGINT NULL, priority INTEGER NULL,
            trigger_state TEXT NOT NULL, trigger_type TEXT NOT NULL, start_time BIGINT NOT NULL, end_time BIGINT NULL,
            calendar_name TEXT NULL, misfire_instr SMALLINT NULL, misfire_orig_fire_time BIGINT NULL,
            execution_group VARCHAR(200) NULL, preferred_node VARCHAR(200) NULL,
            preferred_node_auto BOOL NOT NULL DEFAULT FALSE, retry_policy VARCHAR(250) NULL,
            retry_attempt INTEGER NULL, job_data BYTEA NULL,
            PRIMARY KEY (sched_name, trigger_name, trigger_group),
            FOREIGN KEY (sched_name, job_name, job_group) REFERENCES platform.qrtz_job_details (sched_name, job_name, job_group));

        CREATE TABLE platform.qrtz_simple_triggers (
            sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL,
            repeat_count BIGINT NOT NULL, repeat_interval BIGINT NOT NULL, times_triggered BIGINT NOT NULL,
            PRIMARY KEY (sched_name, trigger_name, trigger_group),
            FOREIGN KEY (sched_name, trigger_name, trigger_group)
                REFERENCES platform.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE);

        CREATE TABLE platform.qrtz_simprop_triggers (
            sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL,
            str_prop_1 TEXT NULL, str_prop_2 TEXT NULL, str_prop_3 TEXT NULL,
            int_prop_1 INTEGER NULL, int_prop_2 INTEGER NULL, long_prop_1 BIGINT NULL, long_prop_2 BIGINT NULL,
            dec_prop_1 NUMERIC NULL, dec_prop_2 NUMERIC NULL, bool_prop_1 BOOL NULL, bool_prop_2 BOOL NULL,
            time_zone_id TEXT NULL,
            PRIMARY KEY (sched_name, trigger_name, trigger_group),
            FOREIGN KEY (sched_name, trigger_name, trigger_group)
                REFERENCES platform.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE);

        CREATE TABLE platform.qrtz_cron_triggers (
            sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL,
            cron_expression TEXT NOT NULL, time_zone_id TEXT,
            PRIMARY KEY (sched_name, trigger_name, trigger_group),
            FOREIGN KEY (sched_name, trigger_name, trigger_group)
                REFERENCES platform.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE);

        CREATE TABLE platform.qrtz_blob_triggers (
            sched_name TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL, blob_data BYTEA NULL,
            PRIMARY KEY (sched_name, trigger_name, trigger_group),
            FOREIGN KEY (sched_name, trigger_name, trigger_group)
                REFERENCES platform.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE);

        CREATE TABLE platform.qrtz_calendars (
            sched_name TEXT NOT NULL, calendar_name TEXT NOT NULL, calendar BYTEA NOT NULL,
            PRIMARY KEY (sched_name, calendar_name));

        CREATE TABLE platform.qrtz_paused_trigger_grps (
            sched_name TEXT NOT NULL, trigger_group TEXT NOT NULL, PRIMARY KEY (sched_name, trigger_group));

        CREATE TABLE platform.qrtz_paused_job_grps (
            sched_name TEXT NOT NULL, job_group TEXT NOT NULL, PRIMARY KEY (sched_name, job_group));

        CREATE TABLE platform.qrtz_fired_triggers (
            sched_name TEXT NOT NULL, entry_id TEXT NOT NULL, trigger_name TEXT NOT NULL, trigger_group TEXT NOT NULL,
            instance_name TEXT NOT NULL, fired_time BIGINT NOT NULL, sched_time BIGINT NOT NULL, priority INTEGER NOT NULL,
            state TEXT NOT NULL, job_name TEXT NULL, job_group TEXT NULL, is_nonconcurrent BOOL NOT NULL,
            requests_recovery BOOL NULL, execution_group VARCHAR(200) NULL,
            PRIMARY KEY (sched_name, entry_id));

        CREATE TABLE platform.qrtz_scheduler_state (
            sched_name TEXT NOT NULL, instance_name TEXT NOT NULL, last_checkin_time BIGINT NOT NULL,
            checkin_interval BIGINT NOT NULL, PRIMARY KEY (sched_name, instance_name));

        CREATE TABLE platform.qrtz_locks (
            sched_name TEXT NOT NULL, lock_name TEXT NOT NULL, PRIMARY KEY (sched_name, lock_name));

        CREATE INDEX idx_qrtz_j_g_n ON platform.qrtz_job_details (sched_name, job_group, job_name);
        CREATE INDEX idx_qrtz_t_j ON platform.qrtz_triggers (sched_name, job_name, job_group);
        CREATE INDEX idx_qrtz_t_c ON platform.qrtz_triggers (sched_name, calendar_name);
        CREATE INDEX idx_qrtz_t_g_n ON platform.qrtz_triggers (sched_name, trigger_group, trigger_name);
        CREATE INDEX idx_qrtz_t_nft_st ON platform.qrtz_triggers (sched_name, trigger_state, next_fire_time ASC, priority DESC, misfire_instr);
        CREATE INDEX idx_qrtz_ft_inst_job_req_rcvry ON platform.qrtz_fired_triggers (sched_name, instance_name, requests_recovery);
        CREATE INDEX idx_qrtz_ft_j_g ON platform.qrtz_fired_triggers (sched_name, job_name, job_group);
        CREATE INDEX idx_qrtz_ft_t_g ON platform.qrtz_fired_triggers (sched_name, trigger_name, trigger_group);
        """;

    /// <summary>
    /// 4.1.1 → 4.6.2: the columns of upstream <c>database/migrations/4.2/add_continuations</c>,
    /// <c>4.3/add_fire_progress</c>, <c>4.3/add_overlap_policy</c>, <c>4.3/add_pause_reason</c>,
    /// <c>4.6/add_scheduler_version</c>, <c>4.6/add_fired_trigger_interrupt</c> and
    /// <c>4.6/add_trigger_misfire_threshold</c> (<c>_postgres.sql</c>, tag v4.6.2), all nullable.
    /// </summary>
    public const string Upgrade46 = """
        ALTER TABLE platform.qrtz_triggers
            ADD COLUMN continues_trigger_name TEXT NULL, ADD COLUMN continues_trigger_group TEXT NULL,
            ADD COLUMN continuation_condition INTEGER NULL, ADD COLUMN overlap_policy INTEGER NULL,
            ADD COLUMN pause_reason VARCHAR(250) NULL, ADD COLUMN paused_by VARCHAR(200) NULL,
            ADD COLUMN paused_at BIGINT NULL, ADD COLUMN misfire_threshold BIGINT NULL;
        ALTER TABLE platform.qrtz_fired_triggers
            ADD COLUMN progress INTEGER NULL, ADD COLUMN progress_message VARCHAR(250) NULL,
            ADD COLUMN interrupt_requested_time BIGINT NULL;
        ALTER TABLE platform.qrtz_paused_trigger_grps
            ADD COLUMN pause_reason VARCHAR(250) NULL, ADD COLUMN paused_by VARCHAR(200) NULL, ADD COLUMN paused_at BIGINT NULL;
        ALTER TABLE platform.qrtz_paused_job_grps
            ADD COLUMN pause_reason VARCHAR(250) NULL, ADD COLUMN paused_by VARCHAR(200) NULL, ADD COLUMN paused_at BIGINT NULL;
        ALTER TABLE platform.qrtz_scheduler_state ADD COLUMN quartz_version VARCHAR(20) NULL;
        """;

    public const string Downgrade46 = """
        ALTER TABLE platform.qrtz_triggers
            DROP COLUMN continues_trigger_name, DROP COLUMN continues_trigger_group, DROP COLUMN continuation_condition,
            DROP COLUMN overlap_policy, DROP COLUMN pause_reason, DROP COLUMN paused_by, DROP COLUMN paused_at,
            DROP COLUMN misfire_threshold;
        ALTER TABLE platform.qrtz_fired_triggers
            DROP COLUMN progress, DROP COLUMN progress_message, DROP COLUMN interrupt_requested_time;
        ALTER TABLE platform.qrtz_paused_trigger_grps DROP COLUMN pause_reason, DROP COLUMN paused_by, DROP COLUMN paused_at;
        ALTER TABLE platform.qrtz_paused_job_grps DROP COLUMN pause_reason, DROP COLUMN paused_by, DROP COLUMN paused_at;
        ALTER TABLE platform.qrtz_scheduler_state DROP COLUMN quartz_version;
        """;

    public const string Drop = """
        DROP TABLE IF EXISTS platform.qrtz_fired_triggers, platform.qrtz_paused_trigger_grps, platform.qrtz_paused_job_grps,
            platform.qrtz_scheduler_state, platform.qrtz_locks, platform.qrtz_simprop_triggers, platform.qrtz_simple_triggers,
            platform.qrtz_cron_triggers, platform.qrtz_blob_triggers, platform.qrtz_triggers, platform.qrtz_job_details,
            platform.qrtz_calendars;
        """;
}
