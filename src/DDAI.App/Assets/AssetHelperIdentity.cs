using System.Security.Cryptography;
using System.Text.Json;

namespace DDAI.App.Assets;

internal static class AssetHelperIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static IDisposable Verify(string mailboxRoot, string executablePath, string? trustedRootOverride = null)
    {
        IDisposable? mailboxLease = null;
        IDisposable? helperRootLease = null;
        FileStream? executableLease = null;
        try
        {
            var mailbox = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mailboxRoot));
            var receiptPath = Path.Combine(mailbox, "private", "asset-helper.json");
            var mailboxFileSystem = new SafeLocalFileSystem(mailbox);
            mailboxLease = mailboxFileSystem.AcquireDirectoryLease(Path.GetDirectoryName(receiptPath)!);
            var receipt = JsonSerializer.Deserialize<Receipt>(mailboxFileSystem.ReadBounded(receiptPath, 4096), JsonOptions)
                ?? throw new InvalidDataException("The asset-helper receipt is missing.");
            var current = Path.GetFullPath(executablePath);
            var expected = Path.GetFullPath(receipt.ExecutablePath ?? string.Empty);
            var localHelpers = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRootOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDAI", "helpers")));
            var underTrustedRoot = expected.StartsWith(localHelpers + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var fileName = Path.GetFileName(expected);
            if (receipt.SchemaVersion != "1.0" || receipt.Owner != "org.ddai.connector" ||
                !current.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                !underTrustedRoot ||
                fileName != "ddai-" + receipt.Sha256 + ".exe" || !IsHash(receipt.Sha256))
            {
                throw new InvalidDataException("The asset-helper identity receipt is invalid.");
            }

            var helperFileSystem = new SafeLocalFileSystem(localHelpers);
            helperRootLease = helperFileSystem.AcquireDirectoryLease(Path.GetDirectoryName(expected)!);
            executableLease = helperFileSystem.OpenReadLease(expected, int.MaxValue);
            var hash = Convert.ToHexString(SHA256.HashData(executableLease)).ToLowerInvariant();
            if (hash != receipt.Sha256)
            {
                throw new InvalidDataException("The asset-helper executable hash changed.");
            }
            executableLease.Position = 0;
            return new IdentityLease(executableLease, helperRootLease, mailboxLease);
        }
        catch
        {
            executableLease?.Dispose();
            helperRootLease?.Dispose();
            mailboxLease?.Dispose();
            throw;
        }
    }

    private static bool IsHash(string? value) => value is not null && value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record Receipt(string? SchemaVersion, string? Owner, string? ExecutablePath, string? Sha256);

    private sealed class IdentityLease(params IDisposable[] leases) : IDisposable
    {
        public void Dispose()
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }
}
