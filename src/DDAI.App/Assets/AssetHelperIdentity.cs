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

    public static void Verify(string mailboxRoot, string executablePath, string? trustedRootOverride = null)
    {
        var receiptPath = Path.Combine(Path.GetFullPath(mailboxRoot), "private", "asset-helper.json");
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(receiptPath), JsonOptions)
            ?? throw new InvalidDataException("The asset-helper receipt is missing.");
        var current = Path.GetFullPath(executablePath);
        var expected = Path.GetFullPath(receipt.ExecutablePath);
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

        using var stream = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (hash != receipt.Sha256)
        {
            throw new InvalidDataException("The asset-helper executable hash changed.");
        }
    }

    private static bool IsHash(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record Receipt(string SchemaVersion, string Owner, string ExecutablePath, string Sha256);
}
