using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using SmartMetrix.Contracts;
using SmartMetrix.Domain;
using SmartMetrix.Messaging;

namespace SmartMetrix.TriggerService;

public sealed class TriggerOptions
{
    public const string SectionName = "Trigger";

    [Range(0, double.MaxValue)] public double MaximumCanSpeedMetresPerSecond { get; set; } = 0.05;
    [Range(0, double.MaxValue)] public double MaximumEncoderSpeedMetresPerSecond { get; set; } = 0.05;
    [Range(0, double.MaxValue)] public double MaximumVibrationRmsMetresPerSecondSquared { get; set; } = 0.15;
    [Range(0, double.MaxValue)] public double MaximumAngularVelocityDegreesPerSecond { get; set; } = 0.5;
    [Range(0, double.MaxValue)] public double MinimumDistanceMetres { get; set; } = 12;
    [Range(0, double.MaxValue)] public double MaximumDistanceMetres { get; set; } = 30;
    [Range(0, int.MaxValue)] public int DebounceMilliseconds { get; set; } = 1000;
    [Range(0, int.MaxValue)] public int CooldownSeconds { get; set; } = 10;
}

public sealed class TriggerOptionsValidator : IValidateOptions<TriggerOptions>
{
    public ValidateOptionsResult Validate(string? name, TriggerOptions options) =>
        options.MaximumDistanceMetres <= options.MinimumDistanceMetres
            ? ValidateOptionsResult.Fail("Trigger:MaximumDistanceMetres must be greater than Trigger:MinimumDistanceMetres.")
            : ValidateOptionsResult.Success;
}

public sealed record TriggerSnapshot(
    double CanSpeedMetresPerSecond,
    double EncoderSpeedMetresPerSecond,
    double VibrationRmsMetresPerSecondSquared,
    double AngularVelocityDegreesPerSecond,
    double DistanceMetres,
    bool CamerasReady,
    bool AutomaticRequest,
    bool ManualCommand,
    bool ManualInhibit);

public sealed record TriggerDecision(
    bool Accepted,
    string Reason,
    DateTimeOffset EvaluatedAt,
    TriggerSnapshot Inputs,
    TimeSpan? Remaining);

public sealed record TriggerEvaluationResult(TriggerDecision Decision, Guid? MeasurementId);

public sealed class TriggerDecisionEngine(TimeProvider clock, IOptions<TriggerOptions> configured)
{
    private readonly object sync = new();
    private readonly TriggerOptions options = configured.Value;
    private DateTimeOffset? safeSince;
    private DateTimeOffset? lastAcceptedAt;

    public TriggerDecision Evaluate(TriggerSnapshot input)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            var rejection = GetSafetyRejection(input);
            if (rejection is not null)
            {
                safeSince = null;
                return new(false, rejection, now, input, null);
            }

            if (!input.AutomaticRequest && !input.ManualCommand)
            {
                safeSince = null;
                return new(false, "NoCaptureCommand", now, input, null);
            }

            var cooldown = TimeSpan.FromSeconds(options.CooldownSeconds);
            if (lastAcceptedAt is { } acceptedAt && now - acceptedAt < cooldown)
                return new(false, "CooldownActive", now, input, cooldown - (now - acceptedAt));

            safeSince ??= now;
            var debounce = TimeSpan.FromMilliseconds(options.DebounceMilliseconds);
            if (now - safeSince < debounce)
                return new(false, "Debouncing", now, input, debounce - (now - safeSince));

            safeSince = null;
            lastAcceptedAt = now;
            return new(true, input.ManualCommand ? "ManualCommand" : "AutomaticConditionsSatisfied", now, input, null);
        }
    }

    private string? GetSafetyRejection(TriggerSnapshot input)
    {
        if (input.ManualInhibit) return "ManualInhibitActive";
        if (!AreFinite(input)) return "InvalidSensorReading";
        if (Math.Abs(input.CanSpeedMetresPerSecond) > options.MaximumCanSpeedMetresPerSecond) return "CanMovementDetected";
        if (Math.Abs(input.EncoderSpeedMetresPerSecond) > options.MaximumEncoderSpeedMetresPerSecond) return "EncoderMovementDetected";
        if (input.VibrationRmsMetresPerSecondSquared > options.MaximumVibrationRmsMetresPerSecondSquared ||
            Math.Abs(input.AngularVelocityDegreesPerSecond) > options.MaximumAngularVelocityDegreesPerSecond) return "VibrationTooHigh";
        if (input.DistanceMetres < options.MinimumDistanceMetres || input.DistanceMetres > options.MaximumDistanceMetres) return "DistanceOutOfRange";
        if (!input.CamerasReady) return "CamerasNotReady";
        return null;
    }

    private static bool AreFinite(TriggerSnapshot input) =>
        double.IsFinite(input.CanSpeedMetresPerSecond) &&
        double.IsFinite(input.EncoderSpeedMetresPerSecond) &&
        double.IsFinite(input.VibrationRmsMetresPerSecondSquared) &&
        input.VibrationRmsMetresPerSecondSquared >= 0 &&
        double.IsFinite(input.AngularVelocityDegreesPerSecond) &&
        double.IsFinite(input.DistanceMetres);
}

public sealed class TriggerCoordinator(TriggerDecisionEngine engine, TimeProvider clock, IOutboxStore outbox)
{
    public async Task<TriggerEvaluationResult> EvaluateAsync(TriggerSnapshot snapshot, CancellationToken cancellationToken)
    {
        var decision = engine.Evaluate(snapshot);
        if (!decision.Accepted) return new(decision, null);

        var measurementId = MeasurementId.New();
        var inputs = new CaptureDecisionInputs(
            snapshot.CanSpeedMetresPerSecond,
            snapshot.EncoderSpeedMetresPerSecond,
            snapshot.VibrationRmsMetresPerSecondSquared,
            snapshot.AngularVelocityDegreesPerSecond,
            snapshot.DistanceMetres,
            snapshot.CamerasReady,
            snapshot.ManualCommand,
            snapshot.ManualInhibit);
        var request = new CaptureRequested(measurementId, decision.Reason, decision.EvaluatedAt, inputs);
        var envelope = new EventEnvelope<CaptureRequested>(Guid.NewGuid(), EventEnvelope.CurrentSchemaVersion,
            clock.GetUtcNow(), measurementId.ToString(), request);
        await outbox.EnqueueAsync(envelope, cancellationToken);
        return new(decision, measurementId.Value);
    }
}

public static class TriggerServiceExtensions
{
    public static IServiceCollection AddTriggerService(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IValidateOptions<TriggerOptions>, TriggerOptionsValidator>();
        services.AddOptions<TriggerOptions>()
            .Bind(configuration.GetSection(TriggerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSmartMetrixMessaging();
        services.AddSingleton<TriggerDecisionEngine>();
        services.AddSingleton<TriggerCoordinator>();
        return services;
    }
}
