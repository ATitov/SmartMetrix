using SmartMetrix.Persistence;
namespace SmartMetrix.LocalPositioningService;

public sealed class TransformRegistry(PostgresDatabase? database = null)
{
    private readonly object gate = new();
    private readonly List<TransformDefinition> definitions = [];

    private T InDatabase<T>(Func<TransformRegistry, T> action, bool write)
    {
        using var session = database!.Open("transforms", () => new List<TransformDefinition>());
        var registry = new TransformRegistry();
        registry.definitions.AddRange(session.Value);
        var result = action(registry);
        if (write) { session.Value = registry.definitions; session.Commit(); }
        return result;
    }

    public TransformDefinition Add(TransformDefinition definition)
    {
        if (database is not null) return InDatabase(registry => registry.Add(definition), true);
        if (string.IsNullOrWhiteSpace(definition.ExcavatorId) || string.IsNullOrWhiteSpace(definition.CoordinateSystemId))
            throw new ArgumentException("Excavator and coordinate system identifiers are required.");
        var rotationLength = definition.RotationLengthSquared();
        if (definition.Version <= 0 || !float.IsFinite(rotationLength) || Math.Abs(rotationLength - 1) > .001)
            throw new ArgumentException("Transform version and normalized rotation are required.");
        lock (gate)
        {
            if (definitions.Any(x => x.ExcavatorId == definition.ExcavatorId && x.Version == definition.Version))
                throw new InvalidOperationException("Transform version already exists.");
            definitions.Add(definition);
            return definition;
        }
    }

    public TransformDefinition Get(string excavatorId, DateTimeOffset at)
    {
        if (database is not null) return InDatabase(registry => registry.Get(excavatorId, at), false);
        lock (gate)
            return definitions.Where(x => x.ExcavatorId == excavatorId && x.ValidFrom <= at && (x.ValidTo is null || at < x.ValidTo))
                .OrderByDescending(x => x.Version).FirstOrDefault()
                ?? throw new KeyNotFoundException($"No excavator-to-quarry transform is valid for {excavatorId} at {at:O}.");
    }

    public IReadOnlyList<TransformDefinition> List(string excavatorId)
    {
        if (database is not null) return InDatabase(registry => registry.List(excavatorId), false);
        lock (gate) return definitions.Where(x => x.ExcavatorId == excavatorId).OrderByDescending(x => x.Version).ToArray();
    }
}

internal static class TransformDefinitionExtensions
{
    public static float RotationLengthSquared(this TransformDefinition value) => value.ExcavatorToQuarry.Rotation.LengthSquared();
}
