using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ApiGateway;

public sealed class WorkstationBackups(IOptions<WorkstationOptions> options, IHostEnvironment environment)
{
    private readonly string root = Path.GetFullPath(options.Value.BackupDirectory, environment.ContentRootPath);

    public object[] List()
    {
        if (!Directory.Exists(root)) return [];
        EnsureRegularDirectory();
        return Directory.EnumerateFiles(root, "*.zip").Where(path => WorkstationIdentity.Identifier(Path.GetFileNameWithoutExtension(path)))
            .Select(path => new FileInfo(path)).Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(file => file.LastWriteTimeUtc).Take(100)
            .Select(file => (object)new
            {
                id = Path.GetFileNameWithoutExtension(file.Name),
                sizeBytes = file.Length,
                createdAt = file.LastWriteTimeUtc,
                hasChecksum = File.Exists(file.FullName + ".sha256")
            }).ToArray();
    }

    public async Task<object> VerifyAsync(string id, CancellationToken ct)
    {
        if (!WorkstationIdentity.Identifier(id)) throw new WorkstationApiException(400, "InvalidBackupId");
        if (!Directory.Exists(root)) throw new WorkstationApiException(404, "BackupNotFound");
        EnsureRegularDirectory();
        var path = Path.Combine(root, id + ".zip");
        if (!File.Exists(path) || !File.Exists(path + ".sha256")) throw new WorkstationApiException(404, "BackupOrChecksumNotFound");
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(path + ".sha256").HasFlag(FileAttributes.ReparsePoint))
            throw new WorkstationApiException(400, "BackupLinkRejected");
        if (new FileInfo(path + ".sha256").Length > 256) throw new WorkstationApiException(422, "InvalidChecksum");
        var expected = (await File.ReadAllTextAsync(path + ".sha256", ct)).Trim();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new WorkstationApiException(422, "InvalidChecksum");
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return new
        {
            id,
            verified = actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            sha256 = actual,
            checkedAt = DateTimeOffset.UtcNow,
            restoreTested = false
        };
    }

    private void EnsureRegularDirectory()
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new WorkstationApiException(400, "BackupLinkRejected");
    }
}
