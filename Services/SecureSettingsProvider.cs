using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace PQA.Web.Services;

public sealed class SecureSettingsProvider
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PBM212A1");
    private readonly IReadOnlyDictionary<string, string> values;

    public SecureSettingsProvider(IConfiguration configuration, IWebHostEnvironment environment)
    {
        string settingsPath = Resolve(configuration["SecureConnectionsFile"] ?? "connections.aes", environment.ContentRootPath);
        string keyPath = Resolve(configuration["SecureConnectionsKeyFile"] ?? "connections.key", environment.ContentRootPath);
        if (!File.Exists(settingsPath)) throw new FileNotFoundException("找不到 AES 加密設定。", settingsPath);

        string? environmentKey = Environment.GetEnvironmentVariable("P_QA_AES_KEY");
        string encodedKey = !string.IsNullOrWhiteSpace(environmentKey)
            ? environmentKey.Trim()
            : File.Exists(keyPath) ? File.ReadAllText(keyPath, Encoding.ASCII).Trim()
            : throw new FileNotFoundException("找不到 AES 金鑰檔，且未設定 P_QA_AES_KEY。", keyPath);
        byte[] key;
        try { key = Convert.FromBase64String(encodedKey); }
        catch (FormatException ex) { throw new InvalidDataException("AES 金鑰格式錯誤。", ex); }
        if (key.Length != 64) throw new InvalidDataException("AES 金鑰必須是 64 位元組。");

        byte[] envelope = File.ReadAllBytes(settingsPath);
        byte[] clear;
        try { clear = Decrypt(envelope, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(clear)
                ?? throw new InvalidDataException("AES 設定內容格式錯誤。");
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public string Get(string name)
    {
        if (!values.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            throw new KeyNotFoundException($"找不到安全設定：{name}");
        if (!name.Equals("MDTE", StringComparison.OrdinalIgnoreCase) && !name.Equals("MD2", StringComparison.OrdinalIgnoreCase)) return value;
        var builder = new SqlConnectionStringBuilder(value) { Encrypt = SqlConnectionEncryptOption.Optional };
        return builder.ConnectionString;
    }

    public string? TryGet(string name) => values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    private static string Resolve(string value, string root) => Path.IsPathRooted(value) ? value : Path.Combine(root, value);

    private static byte[] Decrypt(byte[] envelope, byte[] key)
    {
        const int ivSize = 16, tagSize = 32;
        if (envelope.Length < Magic.Length + ivSize + 16 + tagSize || !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("AES 密文格式或版本錯誤。");
        int authenticatedLength = envelope.Length - tagSize;
        byte[] tag = HMACSHA256.HashData(key.AsSpan(32, 32), envelope.AsSpan(0, authenticatedLength));
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(tag, envelope.AsSpan(authenticatedLength, tagSize)))
                throw new CryptographicException("AES 密文驗證失敗，金鑰不符或檔案已被修改。");
        }
        finally { CryptographicOperations.ZeroMemory(tag); }

        byte[] aesKey = key.AsSpan(0, 32).ToArray();
        byte[] iv = envelope.AsSpan(Magic.Length, ivSize).ToArray();
        byte[] cipher = envelope.AsSpan(Magic.Length + ivSize, authenticatedLength - Magic.Length - ivSize).ToArray();
        try
        {
            using Aes aes = Aes.Create();
            aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            aes.Key = aesKey; aes.IV = iv;
            using ICryptoTransform decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }
}
