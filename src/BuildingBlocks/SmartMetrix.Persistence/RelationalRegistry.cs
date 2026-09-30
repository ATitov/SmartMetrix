using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace SmartMetrix.Persistence;

/// <summary>
/// Maps existing service contracts to entity and journal rows. Nested algorithm parameters
/// remain JSONB; a registry is never stored as a single growing document. Unchanged rows
/// are not rewritten. The enclosing StateSession owns the transaction and writer lock.
/// </summary>
internal static class RelationalRegistry
{
    private sealed record Part(string Table, string? Property, string? Identity, bool AppendOnly = false, bool PointVersions = false);
    private sealed record Layout(Part[] Parts, bool Single = false);

    private static Layout? Find(string schema, string key) => (schema, key) switch
    {
        ("operator", "users") => new([new("accounts", null, "username")]),
        ("operator", "reviews") => new([new("review_entries", null, "commandId", true)]),
        ("operator", "runtime-config-history") => new([new("configuration_revisions", null, null, true)]),
        ("calibration", "registry") => new([new("calibrations", "records", "id"), new("audit_entries", "audit", null, true)]),
        ("control_point", "registry") => new([
            new("point_versions", "points", "version", PointVersions: true),
            new("observations", "observations", "id", true), new("usages", "usages", null, true), new("audit_entries", "audit", null, true)]),
        ("positioning", "transforms") => new([new("transforms", null, "excavatorId/version", true)]),
        ("cloud_sync", "queue") => new([new("sync_items", "items", "id"), new("audit_entries", "audit", null, true)]),
        ("camera", _) when key.StartsWith("capture:", StringComparison.Ordinal) => new([new("captures", null, null)], true),
        ("storage", _) when key.StartsWith("artifact:", StringComparison.Ordinal) => new([new("artifact_entries", null, null, true)], true),
        _ => null
    };

    public static bool Handles(string schema, string key) => Find(schema, key) is not null;

    public static async Task<string?> ReadAsync(string schema, string key, NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var layout = Find(schema, key)!;
        if (!layout.Single)
        {
            await using var exists = new NpgsqlCommand($"SELECT 1 FROM {schema}.registry_versions WHERE key=$1", connection, transaction);
            exists.Parameters.AddWithValue(key);
            if (await exists.ExecuteScalarAsync(ct) is null) return null;
        }
        JsonNode? result = null;
        foreach (var part in layout.Parts)
        {
            await using var command = new NpgsqlCommand($"SELECT group_key,payload::text FROM {schema}.{part.Table}" +
                (layout.Single ? " WHERE key=$1" : " ORDER BY ordinal,key"), connection, transaction);
            if (layout.Single) command.Parameters.AddWithValue(key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            JsonNode rows = part.PointVersions ? new JsonObject() : new JsonArray();
            while (await reader.ReadAsync(ct))
            {
                var value = JsonNode.Parse(reader.GetString(1))!;
                if (layout.Single) return value.ToJsonString();
                if (part.PointVersions)
                {
                    var group = reader.GetString(0);
                    rows[group] ??= new JsonArray();
                    rows[group]!.AsArray().Add(value);
                }
                else rows.AsArray().Add(value);
            }
            if (layout.Single) return null;
            if (part.Property is null) result = rows;
            else { result ??= new JsonObject(); result[part.Property] = rows; }
        }
        return result!.ToJsonString();
    }

    public static async Task WriteAsync(string schema, string key, string json, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var layout = Find(schema, key)!;
        var root = JsonNode.Parse(json)!;
        foreach (var part in layout.Parts)
        {
            var rows = new JsonArray();
            long ordinal = 0;
            void Add(JsonNode value, string? group = null)
            {
                var identity = layout.Single ? key : part.Identity is null ? (++ordinal).ToString(CultureInfo.InvariantCulture)
                    : string.Join('/', part.Identity.Split('/').Select(field => value[field]!.ToString()));
                if (part.Identity is not null) ordinal++;
                if (part.PointVersions) identity = group + "/" + identity;
                // Account and control point identifiers use the same case-insensitive semantics as their APIs.
                if (part.Table == "accounts" || part.PointVersions) identity = identity.ToLowerInvariant();
                rows.Add(new JsonObject { ["key"] = identity, ["group"] = group, ["ordinal"] = ordinal, ["payload"] = value.DeepClone() });
            }
            if (layout.Single) Add(root);
            else if (part.PointVersions)
                foreach (var group in root[part.Property!]!.AsObject())
                    foreach (var value in group.Value!.AsArray()) Add(value!, group.Key);
            else
                foreach (var value in (part.Property is null ? root : root[part.Property]!).AsArray()) Add(value!);

            var serialized = rows.ToJsonString();
            if (part.AppendOnly)
            {
                await using var conflicts = new NpgsqlCommand($"""
                    SELECT EXISTS(SELECT 1 FROM {schema}.{part.Table} t
                    JOIN jsonb_array_elements($1::jsonb) r ON t.key=r->>'key'
                    WHERE t.payload IS DISTINCT FROM r->'payload')
                    """, connection, transaction);
                conflicts.Parameters.AddWithValue(NpgsqlDbType.Jsonb, serialized);
                if ((bool)(await conflicts.ExecuteScalarAsync(ct))!) throw new InvalidOperationException($"Immutable {schema}.{part.Table} entry cannot be changed.");
            }
            var extraColumns = part.Table == "sync_items" ? ",next_attempt_at,created_at" : "";
            var extraValues = part.Table == "sync_items" ? ",(r#>>'{payload,nextAttemptAt}')::timestamptz,(r#>>'{payload,createdAt}')::timestamptz" : "";
            var extraUpdate = part.Table == "sync_items" ? ",next_attempt_at=EXCLUDED.next_attempt_at" : "";
            await using (var write = new NpgsqlCommand($"""
                INSERT INTO {schema}.{part.Table}(key,group_key,ordinal,payload{extraColumns})
                SELECT r->>'key',r->>'group',(r->>'ordinal')::bigint,r->'payload'{extraValues}
                FROM jsonb_array_elements($1::jsonb) r
                ON CONFLICT(key) DO UPDATE SET payload=EXCLUDED.payload,group_key=EXCLUDED.group_key,ordinal=EXCLUDED.ordinal{extraUpdate}
                WHERE {schema}.{part.Table}.payload IS DISTINCT FROM EXCLUDED.payload
                """, connection, transaction))
            {
                write.Parameters.AddWithValue(NpgsqlDbType.Jsonb, serialized);
                await write.ExecuteNonQueryAsync(ct);
            }
            if (!layout.Single && !part.AppendOnly)
            {
                await using var remove = new NpgsqlCommand($"DELETE FROM {schema}.{part.Table} t WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements($1::jsonb) r WHERE r->>'key'=t.key)", connection, transaction);
                remove.Parameters.AddWithValue(NpgsqlDbType.Jsonb, serialized);
                await remove.ExecuteNonQueryAsync(ct);
            }
        }
        if (!layout.Single)
        {
            await using var mark = new NpgsqlCommand($"INSERT INTO {schema}.registry_versions(key) VALUES($1) ON CONFLICT(key) DO UPDATE SET revision={schema}.registry_versions.revision+1", connection, transaction);
            mark.Parameters.AddWithValue(key);
            await mark.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task UpgradeAsync(string schema, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var legacy = new List<(string Key, string Json)>();
        await using (var command = new NpgsqlCommand($"SELECT key,payload::text FROM {schema}.documents", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (Handles(schema, reader.GetString(0))) legacy.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var (key, json) in legacy)
        {
            await using var guard = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {schema}.legacy_documents WHERE key=$1)", connection, transaction);
            guard.Parameters.AddWithValue(key);
            if ((bool)(await guard.ExecuteScalarAsync(ct))!)
                throw new InvalidOperationException("A legacy writer recreated an already migrated registry. Stop old service versions before upgrading.");
            await WriteAsync(schema, key, json, connection, transaction, ct);
            await using var archive = new NpgsqlCommand($"""
                WITH moved AS (DELETE FROM {schema}.documents WHERE key=$1 RETURNING *)
                INSERT INTO {schema}.legacy_documents SELECT * FROM moved;
                """, connection, transaction);
            archive.Parameters.AddWithValue(key);
            await archive.ExecuteNonQueryAsync(ct);
        }
    }
}
