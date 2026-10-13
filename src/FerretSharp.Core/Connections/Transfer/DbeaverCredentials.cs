using System.Security.Cryptography;
using System.Text.Json;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>
/// The user names in DBeaver's <c>credentials-config.json</c> (WP-35, ADR 0025). DBeaver keeps the user of a connection
/// only there, encrypted with AES-128-CBC under a key built into DBeaver (public in its documentation); the first 16 bytes
/// are the IV. The JSON inside maps connection id → <c>#connection</c> → <c>user</c>/<c>password</c>.
/// <para>
/// Only <c>user</c> is taken. A password in the file is decrypted along with it – the whole file is one block – but never
/// read into a string, kept, shown or logged; the decrypted bytes are wiped before returning.
/// </para>
/// </summary>
public static class DbeaverCredentials
{
    /// <summary>DBeaver's built-in key (<c>babb4a9f774ab853c96c2d653dfe544a</c>).</summary>
    private static ReadOnlySpan<byte> Key =>
        [0xba, 0xbb, 0x4a, 0x9f, 0x77, 0x4a, 0xb8, 0x53, 0xc9, 0x6c, 0x2d, 0x65, 0x3d, 0xfe, 0x54, 0x4a];

    private const int IvLength = 16;

    /// <returns>Connection id → user; empty if the file cannot be decrypted or read (the users are then typed in the preview).</returns>
    public static IReadOnlyDictionary<string, string> ReadUsers(byte[]? encrypted)
    {
        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        if (encrypted is null || encrypted.Length <= IvLength || (encrypted.Length - IvLength) % 16 != 0)
        {
            return users;
        }

        byte[]? plain = null;
        try
        {
            using var aes = Aes.Create();
            aes.Key = Key.ToArray();
            plain = aes.DecryptCbc(encrypted.AsSpan(IvLength), encrypted.AsSpan(0, IvLength));
            ReadUsers(plain, users);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            users.Clear();
        }
        finally
        {
            if (plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        return users;
    }

    /// <summary>Walks the JSON with a reader so only the <c>user</c> values ever become strings.</summary>
    private static void ReadUsers(byte[] json, Dictionary<string, string> users)
    {
        var reader = new Utf8JsonReader(json);
        string? connection = null;
        var depth = 0;
        var inConnectionBlock = false;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                    depth--;
                    if (depth < 3)
                    {
                        inConnectionBlock = false;
                    }

                    break;
                case JsonTokenType.PropertyName when depth == 1:
                    connection = reader.GetString();
                    break;
                case JsonTokenType.PropertyName when depth == 2:
                    inConnectionBlock = reader.ValueTextEquals("#connection");
                    break;
                case JsonTokenType.PropertyName when depth == 3 && inConnectionBlock && reader.ValueTextEquals("user"):
                    if (reader.Read() && reader.TokenType == JsonTokenType.String && connection is not null)
                    {
                        users[connection] = reader.GetString() ?? "";
                    }

                    break;
                case JsonTokenType.PropertyName:
                    // Any other value (the password among them) is skipped without being read.
                    reader.Skip();
                    break;
            }
        }
    }

    /// <summary>
    /// The other way round, for tests only: what DBeaver would write for these users. Lives here so the tests need no
    /// copy of the key; never called by the app.
    /// </summary>
    internal static byte[] EncryptForTests(string json, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = Key.ToArray();
        var cipher = aes.EncryptCbc(System.Text.Encoding.UTF8.GetBytes(json), iv);
        return [.. iv, .. cipher];
    }
}
