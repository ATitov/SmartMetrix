namespace SmartMetrix.LocalPositioningService;

public sealed class TransformRegistry
{
    private readonly object gate = new();
    private readonly List<TransformDefinition> definitions = [];

    public TransformDefinition Add(TransformDefinition definition)
    {
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
        lock (gate)
            return definitions.Where(x => x.ExcavatorId == excavatorId && x.ValidFrom <= at && (x.ValidTo is null || at < x.ValidTo))
                .OrderByDescending(x => x.Version).FirstOrDefault()
                ?? throw new KeyNotFoundException($"No excavator-to-quarry transform is valid for {excavatorId} at {at:O}.");
    }
}

internal static class TransformDefinitionExtensions
{
    public static float RotationLengthSquared(this TransformDefinition value) => value.ExcavatorToQuarry.Rotation.LengthSquared();
}
