using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using SmartMetrix.ApiGateway;

namespace SmartMetrix.ArchitectureTests;

public sealed class UserAccountRequirementsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"smartmetrix-users-{Guid.NewGuid():N}");
    private const string Password = "test-only-password-123";

    [Fact]
    [Trait("Requirement", "API-04")]
    public async Task DisabledAndLockedAccountsCannotLoginAndPasswordResetClearsLock()
    {
        using var store = Store();
        await store.InitializeAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Null(await store.AuthenticateAsync("admin", "wrong-password", CancellationToken.None));
        Assert.Null(await store.AuthenticateAsync("admin", Password, CancellationToken.None));
        await store.ResetPasswordAsync("admin", "replacement-password-123", CancellationToken.None);
        Assert.NotNull(await store.AuthenticateAsync("ADMIN", "replacement-password-123", CancellationToken.None));
        await store.SetEnabledAsync("admin", false, CancellationToken.None);
        Assert.Null(await store.AuthenticateAsync("admin", "replacement-password-123", CancellationToken.None));
    }

    [Fact]
    [Trait("Requirement", "API-04")]
    public async Task AccountsSurviveReopenWithoutExposingPasswordMaterialInListing()
    {
        using (var store = Store())
        {
            await store.InitializeAsync(CancellationToken.None);
            await store.AddAsync(new("operator", "Operator", OperatorRoles.Operator, Password), CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AddAsync(new("OPERATOR", "Duplicate", OperatorRoles.Operator, Password), CancellationToken.None));
        }
        using var reopened = Store();
        await reopened.InitializeAsync(CancellationToken.None);
        var user = await reopened.AuthenticateAsync("operator", Password, CancellationToken.None);
        Assert.NotNull(user);
        Assert.Equal(OperatorRoles.Operator, user.Role);
        var listing = JsonSerializer.Serialize(await reopened.ListAsync(CancellationToken.None));
        Assert.DoesNotContain(Password, listing, StringComparison.Ordinal);
        Assert.DoesNotContain(user.PasswordHash, listing, StringComparison.Ordinal);
        Assert.DoesNotContain(user.Salt, listing, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Requirement", "API-04")]
    [InlineData("ab", "Operator", "operator", "valid-long-password")]
    [InlineData("valid", "", "operator", "valid-long-password")]
    [InlineData("valid", "Operator", "root", "valid-long-password")]
    [InlineData("valid", "Operator", "operator", "short")]
    public async Task InvalidAccountDoesNotChangeRegistry(string username, string display, string role, string password)
    {
        using var store = Store();
        await store.InitializeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(new(username, display, role, password), CancellationToken.None));
        Assert.Single(await store.ListAsync(CancellationToken.None));
    }

    private UserAccountStore Store() => new(Options.Create(new OperatorApiOptions
    {
        UserStorePath = Path.Combine(root, "users.json"),
        BootstrapAdminPassword = Password
    }), new TestEnvironment(root));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
