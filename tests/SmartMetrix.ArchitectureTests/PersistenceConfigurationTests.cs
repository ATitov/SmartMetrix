using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartMetrix.Persistence;

namespace SmartMetrix.ArchitectureTests;

public sealed class PersistenceConfigurationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Measurement")]
    [InlineData("measurement; DROP SCHEMA public")]
    [InlineData("measurement.other")]
    [InlineData("measurement-1")]
    [InlineData("a_schema_name_that_is_far_too_long_for_a_postgresql_identifier_to_accept")]
    public void UnsafeSchemaIsRejectedBeforeOpeningConnection(string schema)
    {
        Assert.Throws<ArgumentException>(() => new PostgresDatabase("Host=localhost", schema));
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("postgres")]
    public void PostgresWithoutConnectionStringFailsInsteadOfFallingBack(string provider)
    {
        var builder = Builder(provider);
        Assert.Throws<InvalidOperationException>(() => builder.AddSmartMetrixPersistence("measurement"));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(PostgresDatabase));
    }

    [Fact]
    public void UnknownProviderFailsInsteadOfSilentlyUsingFiles()
    {
        Assert.Throws<InvalidOperationException>(() => Builder("PostgreSQL-typo").AddSmartMetrixPersistence("measurement"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("File")]
    [InlineData("file")]
    public void FileModeDoesNotRegisterDatabaseOrMigrations(string? provider)
    {
        var builder = Builder(provider);
        Assert.False(builder.AddSmartMetrixPersistence("measurement"));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(PostgresDatabase));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    private static HostApplicationBuilder Builder(string? provider)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = provider,
            ["ConnectionStrings:SmartMetrix"] = null
        });
        return builder;
    }
}
