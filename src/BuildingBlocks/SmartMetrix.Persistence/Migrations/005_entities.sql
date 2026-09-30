CREATE TABLE __schema__.registry_versions(key text PRIMARY KEY,revision bigint NOT NULL DEFAULT 1);
CREATE TABLE __schema__.legacy_documents (LIKE __schema__.documents INCLUDING ALL);
DO $migration$
BEGIN
IF '__schema__' = 'operator' THEN
CREATE TABLE operator.accounts(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    username text GENERATED ALWAYS AS (lower(payload->>'username')) STORED UNIQUE NOT NULL,
    enabled boolean GENERATED ALWAYS AS ((payload->>'enabled')::boolean) STORED NOT NULL,
    role text GENERATED ALWAYS AS (payload->>'role') STORED NOT NULL
);
CREATE INDEX ON operator.accounts(ordinal,key);
CREATE TABLE operator.review_entries(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    command_id uuid GENERATED ALWAYS AS ((payload->>'commandId')::uuid) STORED UNIQUE NOT NULL,
    measurement_id uuid GENERATED ALWAYS AS ((payload->>'measurementId')::uuid) STORED NOT NULL,
    scope_id text GENERATED ALWAYS AS (payload->>'scopeId') STORED NOT NULL,
    result_version bigint GENERATED ALWAYS AS ((payload->>'resultVersion')::bigint) STORED CHECK(result_version>0)
);
CREATE INDEX ON operator.review_entries(ordinal,key);
CREATE TABLE operator.configuration_revisions(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object')
);
CREATE INDEX ON operator.configuration_revisions(ordinal,key);
CREATE INDEX ON operator.review_entries(scope_id,measurement_id,result_version);
CREATE OR REPLACE VIEW operator.reviews AS SELECT measurement_id,scope_id,command_id,result_version,
 payload->>'actor' AS actor,payload->>'decision' AS decision,payload->>'reason' AS reason,payload FROM operator.review_entries;
END IF;
IF '__schema__' = 'calibration' THEN
CREATE TABLE calibration.calibrations(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    id uuid GENERATED ALWAYS AS ((payload->>'id')::uuid) STORED UNIQUE NOT NULL,
    rig_id text GENERATED ALWAYS AS (payload#>>'{payload,rigId}') STORED NOT NULL,
    version integer GENERATED ALWAYS AS ((payload->>'version')::integer) STORED CHECK(version>0),
    status text GENERATED ALWAYS AS (payload->>'status') STORED CHECK(status IN ('Draft','Active','Revoked')),
    UNIQUE(rig_id,version)
);
CREATE INDEX ON calibration.calibrations(ordinal,key);
CREATE TABLE calibration.audit_entries(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object')
);
CREATE INDEX ON calibration.audit_entries(ordinal,key);
END IF;
IF '__schema__' = 'control_point' THEN
CREATE TABLE control_point.point_versions(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    point_id text GENERATED ALWAYS AS (lower(group_key)) STORED NOT NULL,
    version integer GENERATED ALWAYS AS ((payload->>'version')::integer) STORED CHECK(version>0),
    coordinate_system_id text GENERATED ALWAYS AS (payload->>'coordinateSystemId') STORED NOT NULL,
    x_metres double precision GENERATED ALWAYS AS ((payload->>'xMetres')::double precision) STORED,
    y_metres double precision GENERATED ALWAYS AS ((payload->>'yMetres')::double precision) STORED,
    z_metres double precision GENERATED ALWAYS AS ((payload->>'zMetres')::double precision) STORED,
    UNIQUE(point_id,version)
);
CREATE INDEX ON control_point.point_versions(ordinal,key);
CREATE TABLE control_point.observations(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    id uuid GENERATED ALWAYS AS ((payload->>'id')::uuid) STORED UNIQUE NOT NULL,
    point_id text GENERATED ALWAYS AS (lower(payload->>'pointId')) STORED NOT NULL
);
CREATE INDEX ON control_point.observations(ordinal,key);
CREATE TABLE control_point.usages(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    point_id text GENERATED ALWAYS AS (lower(payload->>'pointId')) STORED NOT NULL,
    point_version integer GENERATED ALWAYS AS (nullif((payload->>'pointVersion')::integer,0)) STORED,
    measurement_id uuid GENERATED ALWAYS AS ((payload->>'measurementId')::uuid) STORED NOT NULL,
    FOREIGN KEY(point_id,point_version) REFERENCES control_point.point_versions(point_id,version) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ON control_point.usages(ordinal,key);
CREATE TABLE control_point.audit_entries(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object')
);
CREATE INDEX ON control_point.audit_entries(ordinal,key);
CREATE INDEX ON control_point.point_versions(coordinate_system_id,x_metres,y_metres);
END IF;
IF '__schema__' = 'positioning' THEN
CREATE TABLE positioning.transforms(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    excavator_id text GENERATED ALWAYS AS (payload->>'excavatorId') STORED NOT NULL,
    version integer GENERATED ALWAYS AS ((payload->>'version')::integer) STORED CHECK(version>0),
    coordinate_system_id text GENERATED ALWAYS AS (payload->>'coordinateSystemId') STORED NOT NULL,
    UNIQUE(excavator_id,version)
);
CREATE INDEX ON positioning.transforms(ordinal,key);
END IF;
IF '__schema__' = 'cloud_sync' THEN
CREATE TABLE cloud_sync.sync_items(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    id uuid GENERATED ALWAYS AS ((payload->>'id')::uuid) STORED UNIQUE NOT NULL,
    measurement_id uuid GENERATED ALWAYS AS ((payload->>'measurementId')::uuid) STORED NOT NULL,
    version bigint GENERATED ALWAYS AS ((payload->>'version')::bigint) STORED CHECK(version>0),
    state integer GENERATED ALWAYS AS ((payload->>'state')::integer) STORED CHECK(state BETWEEN 0 AND 4),
    next_attempt_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE(measurement_id,version)
);
CREATE INDEX ON cloud_sync.sync_items(ordinal,key);
CREATE TABLE cloud_sync.audit_entries(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object')
);
CREATE INDEX ON cloud_sync.audit_entries(ordinal,key);
CREATE INDEX ON cloud_sync.sync_items(next_attempt_at,created_at) WHERE state IN(0,1,2);
END IF;
IF '__schema__' = 'camera' THEN
CREATE TABLE camera.captures(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    calibration_id text GENERATED ALWAYS AS (payload->>'calibrationId') STORED
);
CREATE INDEX ON camera.captures(ordinal,key);
END IF;
IF '__schema__' = 'storage' THEN
CREATE TABLE storage.artifact_entries(
    key text PRIMARY KEY, group_key text, ordinal bigint NOT NULL, payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
    owner_id uuid GENERATED ALWAYS AS ((payload->>'measurementId')::uuid) STORED NOT NULL,
    artifact_path text GENERATED ALWAYS AS (payload->>'artifactPath') STORED NOT NULL,
    object_uri text GENERATED ALWAYS AS (payload->>'uri') STORED NOT NULL,
    sha256 text GENERATED ALWAYS AS (payload->>'sha256') STORED CHECK(length(sha256)=64),
    size_bytes bigint GENERATED ALWAYS AS ((payload->>'size')::bigint) STORED CHECK(size_bytes>=0),
    UNIQUE(owner_id,artifact_path)
);
CREATE INDEX ON storage.artifact_entries(ordinal,key);
CREATE OR REPLACE VIEW storage.artifacts AS SELECT owner_id,artifact_path,object_uri,sha256,size_bytes,
 payload->>'contentType' AS content_type,payload->>'provenance' AS provenance,(payload->>'storedAt')::timestamptz AS stored_at
 FROM storage.artifact_entries;
END IF;
END $migration$;
