using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Drift.Protocol;

/// <summary>
/// Authenticated, encrypted JSON frames over one TCP stream. The pairing key is a
/// 256-bit base64url secret shown by the Windows host and entered by a peer.
/// </summary>
public sealed class SecureJsonConnection : IAsyncDisposable
{
    private const int TagLength = 16;
    private const int NonceLength = 12;
    private static readonly byte[] NoncePrefix = "DRFT"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private long _sendCounter;
    private long _receiveCounter;

    private SecureJsonConnection(TcpClient client, byte[] sendKey, byte[] receiveKey)
    {
        _client = client;
        _stream = client.GetStream();
        _reader = new StreamReader(_stream, new UTF8Encoding(false), false, 4096, true);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), 4096, true) { NewLine = "\n", AutoFlush = true };
        _sendKey = sendKey;
        _receiveKey = receiveKey;
    }

    public static async Task<SecureJsonConnection> ConnectAsync(
        TcpClient client, string pairingCode, string peerId, string displayName, CancellationToken cancellationToken)
    {
        var secret = DecodeSecret(pairingCode);
        var clientNonce = RandomNumberGenerator.GetBytes(32);
        var clientNonceText = B64Url(clientNonce);
        var helloProof = Hmac(secret, Encoding.UTF8.GetBytes($"hello\n{peerId}\n{clientNonceText}"));
        await WriteHandshakeAsync(client, new { type = "hello", peerId, displayName, clientNonce = clientNonceText, proof = B64Url(helloProof) }, cancellationToken);

        var response = await ReadHandshakeAsync(client, cancellationToken);
        Require(response.RootElement.GetProperty("type").GetString() == "welcome", "Invalid welcome message.");
        var serverNonceText = response.RootElement.GetProperty("serverNonce").GetString()!;
        var serverProof = FromB64Url(response.RootElement.GetProperty("proof").GetString()!);
        var expected = Hmac(secret, Encoding.UTF8.GetBytes($"welcome\n{peerId}\n{clientNonceText}\n{serverNonceText}"));
        Require(CryptographicOperations.FixedTimeEquals(serverProof, expected), "Pairing code was rejected by the host.");
        var serverNonce = FromB64Url(serverNonceText);
        var (clientToServer, serverToClient) = DeriveKeys(secret, clientNonce, serverNonce);
        return new SecureJsonConnection(client, clientToServer, serverToClient);
    }

    public static async Task<(SecureJsonConnection Connection, string PeerId, string DisplayName)> AcceptAsync(
        TcpClient client, string pairingCode, CancellationToken cancellationToken)
    {
        var secret = DecodeSecret(pairingCode);
        var hello = await ReadHandshakeAsync(client, cancellationToken);
        Require(hello.RootElement.GetProperty("type").GetString() == "hello", "Invalid hello message.");
        var peerId = hello.RootElement.GetProperty("peerId").GetString()!;
        var displayName = hello.RootElement.GetProperty("displayName").GetString() ?? "Paired device";
        var clientNonceText = hello.RootElement.GetProperty("clientNonce").GetString()!;
        var proof = FromB64Url(hello.RootElement.GetProperty("proof").GetString()!);
        var expected = Hmac(secret, Encoding.UTF8.GetBytes($"hello\n{peerId}\n{clientNonceText}"));
        Require(CryptographicOperations.FixedTimeEquals(proof, expected), "Pairing code was rejected.");

        var serverNonce = RandomNumberGenerator.GetBytes(32);
        var serverNonceText = B64Url(serverNonce);
        var serverProof = Hmac(secret, Encoding.UTF8.GetBytes($"welcome\n{peerId}\n{clientNonceText}\n{serverNonceText}"));
        await WriteHandshakeAsync(client, new { type = "welcome", serverNonce = serverNonceText, proof = B64Url(serverProof) }, cancellationToken);

        var clientNonce = FromB64Url(clientNonceText);
        var (clientToServer, serverToClient) = DeriveKeys(secret, clientNonce, serverNonce);
        return (new SecureJsonConnection(client, serverToClient, clientToServer), peerId, displayName);
    }

    public async Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(message);
        var counter = Interlocked.Increment(ref _sendCounter);
        var nonce = MakeNonce(counter);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(_sendKey, TagLength))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, nonce);

        var frame = JsonSerializer.Serialize(new SecureFrame(counter, Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)), JsonOptions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(frame.AsMemory(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<JsonDocument?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var line = await _reader.ReadLineAsync(cancellationToken);
        if (line is null) return null;
        if (line.Length > 128 * 1024) throw new InvalidDataException("Frame is too large.");
        var frame = JsonSerializer.Deserialize<SecureFrame>(line, JsonOptions) ?? throw new InvalidDataException("Invalid encrypted frame.");
        var expectedCounter = Interlocked.Read(ref _receiveCounter) + 1;
        if (frame.Counter != expectedCounter) throw new CryptographicException("Frame replay or sequence gap.");
        var nonce = MakeNonce(frame.Counter);
        var ciphertext = Convert.FromBase64String(frame.Ciphertext);
        var tag = Convert.FromBase64String(frame.Tag);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(_receiveKey, TagLength))
            aes.Decrypt(nonce, ciphertext, tag, plaintext, nonce);
        Interlocked.Exchange(ref _receiveCounter, frame.Counter);
        return JsonDocument.Parse(plaintext);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _reader.Dispose();
        _writer.Dispose();
        _writeLock.Dispose();
        CryptographicOperations.ZeroMemory(_sendKey);
        CryptographicOperations.ZeroMemory(_receiveKey);
        await ValueTask.CompletedTask;
    }

    public void Close() => _client.Close();

    public static string NewPairingCode() => B64Url(RandomNumberGenerator.GetBytes(32));

    private static byte[] DecodeSecret(string code)
    {
        var secret = FromB64Url(code.Trim());
        Require(secret.Length == 32, "Pairing code must be a 256-bit base64url secret.");
        return secret;
    }

    private static (byte[] ClientToServer, byte[] ServerToClient) DeriveKeys(byte[] secret, byte[] clientNonce, byte[] serverNonce)
    {
        var salt = clientNonce.Concat(serverNonce).ToArray();
        var prk = Hmac(salt, secret);
        return (Expand(prk, "drift-v1/client-to-server"), Expand(prk, "drift-v1/server-to-client"));
    }

    private static byte[] Expand(byte[] prk, string label)
    {
        var input = Encoding.UTF8.GetBytes(label + "\u0001");
        return Hmac(prk, input);
    }

    private static byte[] Hmac(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    private static byte[] MakeNonce(long counter)
    {
        var nonce = new byte[NonceLength];
        NoncePrefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    private static string B64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromB64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    private static async Task WriteHandshakeAsync<T>(TcpClient client, T value, CancellationToken cancellationToken)
    {
        var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 1024, true) { NewLine = "\n", AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), cancellationToken);
    }

    private static async Task<JsonDocument> ReadHandshakeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false), false, 1024, true);
        var line = await reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("Peer closed during handshake.");
        if (line.Length > 8192) throw new InvalidDataException("Handshake is too large.");
        return JsonDocument.Parse(line);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new CryptographicException(message);
    }

    private sealed record SecureFrame(long Counter, string Ciphertext, string Tag);
}
