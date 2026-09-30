CREATE TABLE __schema__.outbox (
    event_id uuid PRIMARY KEY,
    subject text NOT NULL CHECK(subject LIKE 'smartmetrix.v1.%'),
    payload bytea NOT NULL CHECK(octet_length(payload) <= 262144),
    created_at timestamptz NOT NULL,
    published_at timestamptz,
    attempts integer NOT NULL DEFAULT 0 CHECK(attempts>=0),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error text
);
CREATE INDEX ON __schema__.outbox(next_attempt_at,created_at,event_id) WHERE published_at IS NULL;
CREATE TABLE __schema__.inbox (
    consumer text NOT NULL,
    event_id uuid NOT NULL,
    completed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(consumer,event_id)
);
