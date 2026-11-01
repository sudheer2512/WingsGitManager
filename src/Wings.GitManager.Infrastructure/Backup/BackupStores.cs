using System.Buffers.Binary;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;

namespace Wings.GitManager.Infrastructure.Backup;

/// <summary>Destination for backup objects. Implementations must not depend on the Git server.</summary>
public interface IBackupStore
{
    string Name { get; }
    Task PutAsync(string key, string sourceFile, CancellationToken ct);
    Task GetAsync(string key, string destinationFile, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
    Task<HealthComponentDto> CheckHealthAsync(CancellationToken ct);
    /// <summary>Local root when the store is a file system (used for capacity monitoring).</summary>
    string? LocalRoot { get; }
}

/// <summary>Network share / separate volume. Objects are written once (temp + atomic move) and marked read-only.</summary>
public sealed class FileSystemBackupStore(IOptions<BackupOptions> options) : IBackupStore
{
    private readonly string _root = string.IsNullOrWhiteSpace(options.Value.FileSystemRoot) ? "" : Path.GetFullPath(options.Value.FileSystemRoot);
    public string Name => "filesystem";
    public string? LocalRoot => _root;

    private string PathFor(string key)
    {
        if (_root.Length == 0) throw new GitManagerException("backup_not_configured", "Backup:FileSystemRoot is not configured.", 503);
        if (key.Contains("..") || Path.IsPathRooted(key)) throw new GitManagerException(ErrorCodes.PathRejected, "Invalid backup key.");
        return Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    }

    public async Task PutAsync(string key, string sourceFile, CancellationToken ct)
    {
        var dest = PathFor(key);
        if (File.Exists(dest)) throw new GitManagerException("backup_exists", $"Backup object {key} already exists (objects are immutable).", 409);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".partial";
        await using (var src = File.OpenRead(sourceFile))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, ct);
        File.Move(tmp, dest);
        File.SetAttributes(dest, FileAttributes.ReadOnly);
    }

    public async Task GetAsync(string key, string destinationFile, CancellationToken ct)
    {
        var src = PathFor(key);
        if (!File.Exists(src)) throw new NotFoundException($"Backup object {key}");
        await using var s = File.OpenRead(src);
        await using var d = File.Create(destinationFile);
        await s.CopyToAsync(d, ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var p = PathFor(key);
        if (File.Exists(p))
        {
            File.SetAttributes(p, FileAttributes.Normal);
            File.Delete(p);
        }
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(File.Exists(PathFor(key)));

    public Task<HealthComponentDto> CheckHealthAsync(CancellationToken ct)
    {
        if (_root.Length == 0) return Task.FromResult(new HealthComponentDto("backup-store", "NotConfigured", null, null));
        try
        {
            Directory.CreateDirectory(_root);
            var probe = Path.Combine(_root, ".probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult(new HealthComponentDto("backup-store", "Healthy", $"filesystem {_root}", 0));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthComponentDto("backup-store", "Unhealthy", ex.Message, 0));
        }
    }
}

/// <summary>AWS S3 or any S3-compatible object store (MinIO, Ceph, Wasabi...). Supports Object Lock retention.</summary>
public sealed class S3BackupStore : IBackupStore, IDisposable
{
    private readonly S3BackupOptions _o;
    private readonly AmazonS3Client _client;

    public S3BackupStore(IOptions<BackupOptions> options)
    {
        _o = options.Value.S3;
        var config = new AmazonS3Config { ForcePathStyle = _o.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(_o.ServiceUrl)) { config.ServiceURL = _o.ServiceUrl; config.AuthenticationRegion = _o.Region; }
        else config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_o.Region);
        _client = string.IsNullOrEmpty(_o.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(_o.AccessKey, _o.SecretKey), config);
    }

    public string Name => "s3";
    public string? LocalRoot => null;
    private string Key(string key) => _o.Prefix + key;

    public async Task PutAsync(string key, string sourceFile, CancellationToken ct)
    {
        var req = new PutObjectRequest { BucketName = _o.Bucket, Key = Key(key), FilePath = sourceFile, ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256 };
        if (_o.ObjectLockDays is > 0)
        {
            req.ObjectLockMode = ObjectLockMode.Governance;
            req.ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(_o.ObjectLockDays.Value);
        }
        await _client.PutObjectAsync(req, ct);
    }

    public async Task GetAsync(string key, string destinationFile, CancellationToken ct)
    {
        using var resp = await _client.GetObjectAsync(_o.Bucket, Key(key), ct);
        await resp.WriteResponseStreamToFileAsync(destinationFile, false, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct) => await _client.DeleteObjectAsync(_o.Bucket, Key(key), ct);

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try { await _client.GetObjectMetadataAsync(_o.Bucket, Key(key), ct); return true; }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    public async Task<HealthComponentDto> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _o.Bucket, Prefix = _o.Prefix, MaxKeys = 1 }, ct);
            return new HealthComponentDto("backup-store", "Healthy", $"s3 bucket {_o.Bucket}", null);
        }
        catch (Exception ex)
        {
            return new HealthComponentDto("backup-store", "Unhealthy", ex.Message, null);
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Streaming AES-256-GCM file encryption: "WGMB1" | 12-byte base nonce | repeated [u32 length | ciphertext | 16-byte tag].
/// Each chunk's nonce is the base nonce XOR its counter; the final chunk is flagged in the associated data so
/// truncation is detected.
/// </summary>
public static class BackupCrypto
{
    private static readonly byte[] Magic = "WGMB1"u8.ToArray();
    private const int ChunkSize = 1024 * 1024;

    public static byte[] ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return [];
        var key = Convert.FromBase64String(base64);
        if (key.Length != 32) throw new GitManagerException("backup_key_invalid", "Backup:EncryptionKey must be a base64 256-bit key.", 500);
        return key;
    }

    public static async Task EncryptAsync(string input, string output, byte[] key, CancellationToken ct)
    {
        using var aes = new AesGcm(key, 16);
        var baseNonce = RandomNumberGenerator.GetBytes(12);
        await using var src = File.OpenRead(input);
        await using var dst = File.Create(output);
        await dst.WriteAsync(Magic, ct);
        await dst.WriteAsync(baseNonce, ct);
        var buffer = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var read = await ReadFullAsync(src, buffer, ct);
        long counter = 0;
        while (true)
        {
            var nextRead = read == ChunkSize ? await ReadFullAsync(src, next, ct) : 0;
            var final = nextRead == 0;
            var cipher = new byte[read];
            var tag = new byte[16];
            aes.Encrypt(Nonce(baseNonce, counter), buffer.AsSpan(0, read), cipher, tag, final ? [1] : [0]);
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)read);
            await dst.WriteAsync(len, ct);
            await dst.WriteAsync(cipher, ct);
            await dst.WriteAsync(tag, ct);
            if (final) break;
            (buffer, next) = (next, buffer);
            read = nextRead;
            counter++;
        }
    }

    public static async Task DecryptAsync(string input, string output, byte[] key, CancellationToken ct)
    {
        using var aes = new AesGcm(key, 16);
        await using var src = File.OpenRead(input);
        var header = new byte[17];
        if (await ReadFullAsync(src, header, ct) != 17 || !header.AsSpan(0, 5).SequenceEqual(Magic))
            throw new CryptographicException("Not a Wings backup file.");
        var baseNonce = header[5..17];
        await using var dst = File.Create(output);
        long counter = 0;
        var len = new byte[4];
        var sawFinal = false;
        while (await ReadFullAsync(src, len, ct) == 4)
        {
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(len);
            if (n > ChunkSize) throw new CryptographicException("Corrupt chunk length.");
            var cipher = new byte[n];
            var tag = new byte[16];
            if (await ReadFullAsync(src, cipher, ct) != n || await ReadFullAsync(src, tag, ct) != 16) throw new CryptographicException("Truncated backup.");
            var plain = new byte[n];
            var isFinal = src.Position == src.Length;
            aes.Decrypt(Nonce(baseNonce, counter), cipher, tag, plain, isFinal ? [1] : [0]);
            await dst.WriteAsync(plain, ct);
            counter++;
            sawFinal = isFinal;
        }
        if (!sawFinal) throw new CryptographicException("Backup is truncated (final chunk missing).");
    }

    private static byte[] Nonce(byte[] baseNonce, long counter)
    {
        var n = (byte[])baseNonce.Clone();
        Span<byte> c = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(c, counter);
        for (var i = 0; i < 8; i++) n[4 + i] ^= c[i];
        return n;
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var r = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    public static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        await using var s = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
    }
}
