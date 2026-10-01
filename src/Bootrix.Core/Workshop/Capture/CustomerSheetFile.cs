// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Bootrix.Core.Errors;
using Bootrix.Core.Json;

namespace Bootrix.Core.Workshop.Capture;

/// <summary>
/// The file a customer PC's inventory is kept in. Secrets (Wi-Fi keys, recovery passwords, product key) are in it in
/// clear text, so the file is always encrypted: AES-256-GCM with a key derived from a password by PBKDF2-SHA-256.
/// Without a password nothing is written.
/// </summary>
public static class CustomerSheetFile
{
    public const string Extension = ".bootrixsheet";
    public const int MinimumPasswordLength = 8;

    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int MaxPlainBytes = 64 * 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "BTXSHEET"u8;

    // Magic, format version, iteration count and salt are authenticated as additional data, so none of them can be changed.
    private const int HeaderBytes = 8 + 2 + 4 + SaltBytes + NonceBytes;

    public static void Write(Stream output, CustomerPcCapture capture, string password)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(capture);
        RequirePassword(password);

        var plain = JsonSerializer.SerializeToUtf8Bytes(capture, CoreJson.Options);
        var header = new byte[HeaderBytes];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), Iterations);
        RandomNumberGenerator.Fill(header.AsSpan(14, SaltBytes + NonceBytes));

        var key = DeriveKey(password, header.AsSpan(14, SaltBytes), Iterations);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(header.AsSpan(14 + SaltBytes, NonceBytes), plain, cipher, tag, header.AsSpan(0, 14 + SaltBytes));
        }

        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plain);

        output.Write(header);
        output.Write(cipher);
        output.Write(tag);
    }

    /// <exception cref="BootrixException">With <see cref="ErrorCode.InvalidSpec"/> when the file is damaged or the password is wrong.</exception>
    public static CustomerPcCapture Read(Stream input, string password)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequirePassword(password);

        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var data = buffer.ToArray();
        if (data.Length < HeaderBytes + TagBytes || data.Length > MaxPlainBytes || !data.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw Invalid("This is not a customer sheet of Bootrix.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(10));
        if (version != 1 || iterations is < 100_000 or > 10_000_000)
        {
            throw Invalid("The customer sheet is of a version this program does not know.");
        }

        var key = DeriveKey(password, data.AsSpan(14, SaltBytes), iterations);
        var plain = new byte[data.Length - HeaderBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(
                data.AsSpan(14 + SaltBytes, NonceBytes),
                data.AsSpan(HeaderBytes, plain.Length),
                data.AsSpan(HeaderBytes + plain.Length, TagBytes),
                plain,
                data.AsSpan(0, 14 + SaltBytes));
            return JsonSerializer.Deserialize<CustomerPcCapture>(plain, CoreJson.Options) ?? throw Invalid("The customer sheet is empty.");
        }
        catch (CryptographicException)
        {
            throw Invalid("The password is wrong or the customer sheet is damaged.");
        }
        catch (JsonException)
        {
            throw Invalid("The customer sheet is damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    private static void RequirePassword(string password)
    {
        if (password is null || password.Length < MinimumPasswordLength)
        {
            throw Invalid($"The password needs at least {MinimumPasswordLength} characters.");
        }
    }

    private static BootrixException Invalid(string message) => new(ErrorCode.InvalidSpec, message) { Arguments = [message] };
}
