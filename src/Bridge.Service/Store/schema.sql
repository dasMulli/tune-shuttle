-- Bridge SQLite schema (docs/ARCHITECTURE.md, "Persistence"). Applied by SqliteBridgeStore.InitializeAsync; versioned via
-- PRAGMA user_version so later changes can add migrations (StoreMigrations.cs).
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS receivers (
    id              TEXT PRIMARY KEY,            -- deviceid from the TXT record (MAC-style)
    name            TEXT NOT NULL,
    model           TEXT,
    manufacturer    TEXT,
    last_address    TEXT,                        -- last A/AAAA address used
    last_port       INTEGER NOT NULL DEFAULT 7000,
    features        INTEGER NOT NULL DEFAULT 0,  -- 64-bit feature word
    status_flags    INTEGER NOT NULL DEFAULT 0,
    public_key      BLOB,                        -- pk from TXT; a change means the receiver was reset
    pairing_state   TEXT NOT NULL DEFAULT 'transient', -- transient | pin_required | paired | disabled
    level           INTEGER NOT NULL DEFAULT 50, -- r_i when not in a group
    offset_frames   INTEGER NOT NULL DEFAULT 0,  -- per-receiver latency trim (SPEC-audio.md 3.3)
    quirks          TEXT,                        -- JSON ReceiverQuirks overrides
    last_seen       TEXT,                        -- ISO-8601 UTC
    created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE TABLE IF NOT EXISTS pairings (
    receiver_id         TEXT PRIMARY KEY REFERENCES receivers(id) ON DELETE CASCADE,
    controller_id       TEXT NOT NULL,           -- our pairing identifier (TLV Identifier)
    controller_seed     BLOB NOT NULL,           -- our Ed25519 seed (LTSK), 32 bytes
    accessory_id        TEXT NOT NULL,           -- the receiver's identifier from M6
    accessory_public_key BLOB NOT NULL,          -- the receiver's Ed25519 LTPK, 32 bytes
    created_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE TABLE IF NOT EXISTS groups (
    id              TEXT PRIMARY KEY,            -- UUID, also sent as groupUUID in the session SETUP
    name            TEXT NOT NULL,
    master_level    INTEGER NOT NULL DEFAULT 50, -- G
    created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE TABLE IF NOT EXISTS group_members (
    group_id        TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    receiver_id     TEXT NOT NULL REFERENCES receivers(id) ON DELETE CASCADE,
    level           INTEGER NOT NULL DEFAULT 50, -- r_i inside this group
    position        INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (group_id, receiver_id)
);

CREATE TABLE IF NOT EXISTS settings (
    key             TEXT PRIMARY KEY,            -- device_name, selected_output_group, selected_output_receiver,
    value           TEXT                         -- latency_frames, poll_receiver_volume, preferred_stream, controller_identity
);

CREATE TABLE IF NOT EXISTS spotify_credentials (
    username        TEXT PRIMARY KEY,
    auth_type       INTEGER NOT NULL,
    auth_data       BLOB NOT NULL,               -- librespot Credentials (reusable credential blob)
    updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

PRAGMA user_version = 1;
