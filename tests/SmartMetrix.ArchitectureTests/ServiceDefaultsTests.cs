using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMetrix.ServiceDefaults;

namespace SmartMetrix.ArchitectureTests;

public sealed class ServiceDefaultsTests
{
    [Fact]
    public void ConfigurationRejectsMissingServiceName()
    {
        var options = new SmartMetrixServiceOptions { ServiceName = string.Empty };

        var results = Validate(options);

        Assert.Contains(results, result =>
            result.MemberNames.Contains(nameof(SmartMetrixServiceOptions.ServiceName)));
    }

    [Theory]
    [InlineData("ApiGateway")]
    [InlineData("SmartMetrix Service")]
    [InlineData("SmartMetrix.Service/Unsafe")]
    public void ConfigurationRejectsInvalidServiceName(string serviceName)
    {
        var options = new SmartMetrixServiceOptions { ServiceName = serviceName };

        Assert.NotEmpty(Validate(options));
    }

    [Fact]
    public void ConfigurationAcceptsValidValues()
    {
        var options = new SmartMetrixServiceOptions
        {
            ServiceName = "SmartMetrix.CameraService",
            HttpClientTimeoutSeconds = 15,
            ShutdownTimeoutSeconds = 45
        };

        Assert.Empty(Validate(options));
        Assert.Equal(TimeSpan.FromSeconds(15), options.HttpClientTimeout);
    }

    [Theory]
    [InlineData("measurement-0195")]
    [InlineData("trace:123.456")]
    public void CorrelationContextAcceptsSafeValues(string value)
    {
        Assert.True(CorrelationContext.IsValid(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains whitespace")]
    [InlineData("contains/newline\n")]
    public void CorrelationContextRejectsUnsafeValues(string value)
    {
        Assert.False(CorrelationContext.IsValid(value));
    }

    [Fact]
    public async Task MiddlewarePropagatesCorrelationAndMeasurementContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationContext.CorrelationHeader] = "correlation-123";
        context.Request.Headers[CorrelationContext.MeasurementHeader] = "measurement-456";
        var wasCalled = false;
        var middleware = new CorrelationContextMiddleware(
            _ =>
            {
                wasCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<CorrelationContextMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.True(wasCalled);
        Assert.Equal(
            "correlation-123",
            context.Response.Headers[CorrelationContext.CorrelationHeader].ToString());
    }

    [Fact]
    public void EventConsumerCreatesTraceWithDomainTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SmartMetrixTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = SmartMetrixTelemetry.StartEventProcessing(
            "CaptureCompleted",
            "event-123",
            "correlation-123",
            "measurement-456");

        Assert.NotNull(activity);
        Assert.Equal(ActivityKind.Consumer, activity.Kind);
        Assert.Equal("event-123", activity.GetTagItem("messaging.message.id"));
        Assert.Equal("measurement-456", activity.GetTagItem("smartmetrix.measurement_id"));
    }

    private static List<ValidationResult> Validate(SmartMetrixServiceOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);
        return results;
    }
}
