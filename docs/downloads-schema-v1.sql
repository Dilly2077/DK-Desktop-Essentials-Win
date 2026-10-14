-- DK Desktop Essentials / Downloads & Transfers / Slice 32.1
-- Version 1 proposal. Applied by the future SQLite store (not wired in 32.1).
-- All dates: ISO-8601 UTC strings written by the coordinator, e.g. 2026-10-14T20:00:00.0000000+00:00.
-- DB is local to the signed-in Windows user; never sync or upload it automatically.
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS download_sources (
    source_key       TEXT PRIMARY KEY,
    display_host     TEXT NOT NULL,
    -- Windows DPAPI CurrentUser-protected bytes, not a plaintext URL.
    protected_url    BLOB NOT NULL
);

CREATE TABLE IF NOT EXISTS download_jobs (
    id                TEXT PRIMARY KEY,
    source_key        TEXT NOT NULL REFERENCES download_sources(source_key),
    state             TEXT NOT NULL CHECK (state IN
        ('Queued','Resolving','Downloading','Paused','Retrying','Verifying','Completed','Failed','Cancelled')),
    priority          INTEGER NOT NULL DEFAULT 1 CHECK (priority BETWEEN 0 AND 2),
    destination_path  TEXT NOT NULL,
    temporary_path    TEXT NOT NULL,
    created_utc       TEXT NOT NULL,
    updated_utc       TEXT NOT NULL,
    transferred_bytes INTEGER NOT NULL DEFAULT 0 CHECK (transferred_bytes >= 0),
    expected_bytes    INTEGER CHECK (expected_bytes IS NULL OR expected_bytes >= 0),
    expected_sha256   TEXT,
    verified_sha256   TEXT,
    etag              TEXT,
    last_modified_utc TEXT,
    resume_capability TEXT NOT NULL DEFAULT 'Unknown'
                      CHECK (resume_capability IN ('Unknown','Supported','Unsupported')),
    attempts          INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    next_attempt_utc  TEXT,
    last_error_code   TEXT,
    safe_error_detail TEXT,
    revision          INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
    CHECK (expected_bytes IS NULL OR transferred_bytes <= expected_bytes),
    CHECK (expected_sha256 IS NULL OR length(expected_sha256) = 64),
    CHECK (verified_sha256 IS NULL OR length(verified_sha256) = 64)
);

CREATE INDEX IF NOT EXISTS idx_downloads_dispatch
    ON download_jobs(state, priority DESC, created_utc ASC, id ASC);

CREATE TABLE IF NOT EXISTS download_events (
    event_id       INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id         TEXT NOT NULL REFERENCES download_jobs(id) ON DELETE CASCADE,
    occurred_utc   TEXT NOT NULL,
    from_state     TEXT,
    to_state       TEXT NOT NULL,
    error_code     TEXT,
    -- Never store URLs, request headers, tokens, cookies, or raw exception strings.
    safe_message   TEXT
);

CREATE INDEX IF NOT EXISTS idx_download_events_job
    ON download_events(job_id, event_id);

CREATE TABLE IF NOT EXISTS download_queue_settings (
    singleton              INTEGER PRIMARY KEY CHECK(singleton = 1),
    max_concurrent         INTEGER NOT NULL DEFAULT 3 CHECK(max_concurrent BETWEEN 1 AND 16),
    max_attempts           INTEGER NOT NULL DEFAULT 5 CHECK(max_attempts BETWEEN 1 AND 20),
    auto_start_on_launch   INTEGER NOT NULL DEFAULT 0 CHECK(auto_start_on_launch IN (0,1))
);
INSERT OR IGNORE INTO download_queue_settings
    (singleton, max_concurrent, max_attempts, auto_start_on_launch)
    VALUES (1, 3, 5, 0);

PRAGMA user_version = 1;
