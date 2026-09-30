using SmartMetrix.Persistence;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ApiGateway;

public sealed record UserAccount(string Username, string DisplayName, string Role, string Salt, string PasswordHash,
    bool Enabled, int FailedAttempts, DateTimeOffset? LockedUntil, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt,
    string[]? AdditionalRoles = null, string[]? ScopeIds = null);
public sealed record LoginRequest(string Username, string Password);
public sealed record CreateUserRequest(string Username, string DisplayName, string Role, string Password,
    string[]? AdditionalRoles = null, string[]? ScopeIds = null);
public sealed record UserAccessRequest(string Role, string[] AdditionalRoles, string[] ScopeIds);
public sealed record ResetPasswordRequest(string Password);

public sealed class UserAccountStore(IOptions<OperatorApiOptions> options, IWebHostEnvironment environment, PostgresDatabase? database = null) : IDisposable
{
    private const int Iterations = 210_000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var session = database is null ? null : await database.OpenAsync("users", () => new List<UserAccount>(), ct);
            if (session?.Exists == true || (session is null && File.Exists(PathForStore()))) return;
            if (string.IsNullOrWhiteSpace(options.Value.BootstrapAdminPassword))
                throw new InvalidOperationException("OperatorApi:BootstrapAdminPassword is required for first start.");
            var admin = Create("admin", "Администратор SmartMetrix", OperatorRoles.Administrator,
                options.Value.BootstrapAdminPassword);
            await SaveUnsafeAsync([admin], session, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<UserAccount?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var session = database is null ? null : await database.OpenAsync("users", () => new List<UserAccount>(), ct);
            var users = await LoadUnsafeAsync(session, ct);
            if (string.IsNullOrWhiteSpace(username) || password is null) return null;
            var index = users.FindIndex(item => item.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            var user = users[index];
            if (!user.Enabled || user.LockedUntil > DateTimeOffset.UtcNow) return null;
            if (!Verify(password, user))
            {
                var failures = user.FailedAttempts + 1;
                users[index] = user with { FailedAttempts = failures, LockedUntil = failures >= 5 ? DateTimeOffset.UtcNow.AddMinutes(15) : null };
                await SaveUnsafeAsync(users, session, ct);
                return null;
            }
            user = user with { FailedAttempts = 0, LockedUntil = null, LastLoginAt = DateTimeOffset.UtcNow };
            users[index] = user;
            await SaveUnsafeAsync(users, session, ct);
            return user;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken ct) =>
        (await LoadAsync(ct)).Select(user => (object)new { user.Username, user.DisplayName, user.Role, user.AdditionalRoles, user.ScopeIds, user.Enabled, user.LockedUntil, user.CreatedAt, user.LastLoginAt }).ToArray();

    public async Task<UserAccount?> FindAsync(string username, CancellationToken ct) =>
        (await LoadAsync(ct)).FirstOrDefault(user => user.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    public Task SetAccessAsync(string username, UserAccessRequest request, CancellationToken ct)
    {
        WorkstationIdentity.ValidateAccess(request.Role, request.AdditionalRoles, request.ScopeIds);
        return UpdateAsync(username, user => user with { Role = request.Role, AdditionalRoles = request.AdditionalRoles, ScopeIds = request.ScopeIds }, ct);
    }

    public async Task<UserAccount> AddAsync(CreateUserRequest request, CancellationToken ct)
    {
        Validate(request.Username, request.DisplayName, request.Role, request.Password);
        WorkstationIdentity.ValidateAccess(request.Role, request.AdditionalRoles ?? [], request.ScopeIds ?? []);
        await _gate.WaitAsync(ct);
        try
        {
            await using var session = database is null ? null : await database.OpenAsync("users", () => new List<UserAccount>(), ct);
            var users = await LoadUnsafeAsync(session, ct);
            if (users.Any(item => item.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Пользователь уже существует.");
            var user = Create(request.Username.Trim(), request.DisplayName.Trim(), request.Role, request.Password)
                with
            { AdditionalRoles = request.AdditionalRoles, ScopeIds = request.ScopeIds };
            users.Add(user); await SaveUnsafeAsync(users, session, ct); return user;
        }
        finally { _gate.Release(); }
    }

    public Task SetEnabledAsync(string username, bool enabled, CancellationToken ct) => UpdateAsync(username, user => user with { Enabled = enabled }, ct);
    public Task ResetPasswordAsync(string username, string password, CancellationToken ct)
    {
        if (password is null || password.Length < 12) throw new ArgumentException("Пароль должен содержать не менее 12 символов.");
        return UpdateAsync(username, user => WithPassword(user, password), ct);
    }

    private async Task UpdateAsync(string username, Func<UserAccount, UserAccount> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var session = database is null ? null : await database.OpenAsync("users", () => new List<UserAccount>(), ct);
            var users = await LoadUnsafeAsync(session, ct); var index = users.FindIndex(item => item.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new KeyNotFoundException("Пользователь не найден.");
            users[index] = change(users[index]); await SaveUnsafeAsync(users, session, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<UserAccount>> LoadAsync(CancellationToken ct) { await _gate.WaitAsync(ct); try { await using var session = database is null ? null : await database.OpenAsync("users", () => new List<UserAccount>(), ct); return await LoadUnsafeAsync(session, ct); } finally { _gate.Release(); } }
    private async Task<List<UserAccount>> LoadUnsafeAsync(StateSession<List<UserAccount>>? session, CancellationToken ct) { if (session is not null) return session.Value; await using var stream = File.OpenRead(PathForStore()); return await JsonSerializer.DeserializeAsync<List<UserAccount>>(stream, JsonOptions, ct) ?? []; }
    private async Task SaveUnsafeAsync(List<UserAccount> users, StateSession<List<UserAccount>>? session, CancellationToken ct) { if (session is not null) { session.Value = users; await session.CommitAsync(ct); return; } var path = PathForStore(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp"; await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, users, JsonOptions, ct); File.Move(temporary, path, true); }
    private string PathForStore() => Path.IsPathRooted(options.Value.UserStorePath) ? options.Value.UserStorePath : Path.Combine(environment.ContentRootPath, options.Value.UserStorePath);
    private static UserAccount Create(string username, string display, string role, string password) => WithPassword(new(username, display, role, "", "", true, 0, null, DateTimeOffset.UtcNow, null), password);
    private static UserAccount WithPassword(UserAccount user, string password) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32); return user with { Salt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(hash), FailedAttempts = 0, LockedUntil = null }; }
    private static bool Verify(string password, UserAccount user) { var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(user.Salt), Iterations, HashAlgorithmName.SHA256, 32); return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(user.PasswordHash)); }
    private static void Validate(string username, string display, string role, string password) { if (username is null || username.Length is < 3 or > 64 || username.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '.' and not '-' and not '_')) throw new ArgumentException("Недопустимый логин."); if (string.IsNullOrWhiteSpace(display) || display.Length > 128) throw new ArgumentException("Недопустимое имя."); if (!OperatorRoles.All.Contains(role)) throw new ArgumentException("Недопустимая роль."); if (password is null || password.Length < 12) throw new ArgumentException("Пароль должен содержать не менее 12 символов."); }
    public void Dispose() => _gate.Dispose();
}

public sealed class UserStoreInitializer(UserAccountStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
