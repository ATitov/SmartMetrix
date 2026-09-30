-- Run as administrator after all measurement migrations, with writers stopped.
-- SRID 0 means local coordinates; every query must select coordinate_system_id.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE INDEX IF NOT EXISTS block_records_centre_gist ON measurement.block_records
 USING gist (ST_SetSRID(ST_MakePoint(x_metres,y_metres,z_metres),0))
 WHERE x_metres IS NOT NULL AND y_metres IS NOT NULL AND z_metres IS NOT NULL;
CREATE OR REPLACE VIEW measurement.spatial_blocks AS
 SELECT b.*, ST_SetSRID(ST_MakePoint(x_metres,y_metres,z_metres),0) AS centre
 FROM measurement.blocks b
 WHERE x_metres IS NOT NULL AND y_metres IS NOT NULL AND z_metres IS NOT NULL;
