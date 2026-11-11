using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ElanciaLocal;

internal sealed class SecureGateway(AccountDatabase database)
{
    internal sealed record Connection(string Account, int LoginPort, int WorldPort, CancellationToken Revoked);
    sealed class Session(AccountDatabase.GameSession saved, long now)
    {
        readonly CancellationTokenSource lifetime = new(TimeSpan.FromMilliseconds(saved.ExpiresAt - now));
        internal readonly string Account = saved.Account;
        internal readonly bool LoopbackOnly = saved.LoopbackOnly;
        internal CancellationToken Cancellation => lifetime.Token;
        internal void Revoke() => lifetime.Cancel();
    }
    readonly ConcurrentDictionary<string, Session> sessions = new();
    readonly object sessionGate = new();
    readonly SemaphoreSlim connections = new(256);
    readonly SemaphoreSlim passwordWork = new(4);
    readonly Dictionary<IPAddress, int> attempts = [];
    long attemptMinute = -1;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal bool UpdateAccount(string account, string login, string? password)
    {
        lock (sessionGate) {
            if (!database.UpdateAccount(account, login, password)) return false;
            if (password is not null)
                foreach (var (key, session) in sessions)
                    if (session.Account == account && sessions.TryRemove(key, out _)) session.Revoke();
            return true;
        }
    }

    byte[] Login(string login,string password,bool loopback)
    {
        lock(sessionGate){
            if (database.Authenticate(login,password) is not {} account) return [1];
#if ELANCIA_RELEASE_BUNDLE
            const bool loopbackOnly=false;
#else
            bool loopbackOnly=account=="test"&&password=="test";
            if(loopbackOnly&&!loopback)return [1];
#endif
            foreach (var (key, old) in sessions)
                if (old.Cancellation.IsCancellationRequested) sessions.TryRemove(key, out _);
            byte[] token = RandomNumberGenerator.GetBytes(32);
            string hash = Convert.ToHexString(SHA256.HashData(token));
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var saved = new AccountDatabase.GameSession(account, loopbackOnly, now + (long)TimeSpan.FromHours(12).TotalMilliseconds);
            if (!database.SaveGameSession(hash, saved, now)) {
                CryptographicOperations.ZeroMemory(token);
                return [2];
            }
            sessions[hash] = new(saved, now);
            return [0,..token];
        }
    }

    Session? ResolveSession(string hash)
    {
        if (sessions.TryGetValue(hash, out var cached)) return cached;
        lock (sessionGate) {
            if (sessions.TryGetValue(hash, out cached)) return cached;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (database.FindGameSession(hash, now) is not {} saved) return null;
            return sessions[hash] = new(saved, now);
        }
    }

    bool AllowAttempt(IPAddress ip)
    {
        lock (attempts) {
            long minute = Environment.TickCount64 / 60_000;
            if (minute != attemptMinute) { attempts.Clear(); attemptMinute = minute; }
            if (attempts.Count >= 4096 && !attempts.ContainsKey(ip)) return false;
            int count = attempts.GetValueOrDefault(ip);
            if (count >= 8) return false;
            attempts[ip] = count + 1;
            return true;
        }
    }

    internal async Task Run(IPAddress address, int port, X509Certificate2 cert,
        Func<Stream, string, Connection, Task> game, CancellationToken stop)
    {
        var listener = new TcpListener(address, port);
        listener.Start();
        Console.WriteLine($"Secure launcher listening {address}:{port}");
        Console.WriteLine($"TLS certificate SHA256 {cert.GetCertHashString(HashAlgorithmName.SHA256)}");
        try {
            while (!stop.IsCancellationRequested) {
                var client = await listener.AcceptTcpClientAsync(stop);
                if (!connections.Wait(0)) { client.Dispose(); continue; }
                _ = Handle(client, cert, game, stop);
            }
        } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }

    async Task Handle(TcpClient client, X509Certificate2 cert,
        Func<Stream, string, Connection, Task> game, CancellationToken stop)
    {
        try {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop)) {
                client.NoDelay = true;
                var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                bool loopback = IPAddress.IsLoopback(ip);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var ct = timeout.Token;
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowRenegotiation = false
                }, ct);
                byte[] header = new byte[2];
                await tls.ReadExactlyAsync(header, ct);
                if (header[0] != 1) return;
                if (header[1] == 3) {
                    byte[] route = new byte[37];
                    await tls.ReadExactlyAsync(route, ct);
                    string? role = route[0] switch { 0 => "Bootstrap", 1 => "Login", 2 => "World", _ => null };
                    int loginPort = BinaryPrimitives.ReadUInt16BigEndian(route.AsSpan(33));
                    int worldPort = BinaryPrimitives.ReadUInt16BigEndian(route.AsSpan(35));
                    string hash = Convert.ToHexString(SHA256.HashData(route.AsSpan(1, 32)));
                    CryptographicOperations.ZeroMemory(route.AsSpan(1, 32));
                    if (role is null || loginPort < 1024 || worldPort < 1024 || loginPort == worldPort ||
                        ResolveSession(hash) is not {} session ||
                        session.Cancellation.IsCancellationRequested||session.LoopbackOnly&&!loopback) {
                        await tls.WriteAsync(new byte[]{1}, ct);
                        return;
                    }
                    await tls.WriteAsync(new byte[]{0}, ct);
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await game(tls, role, new(session.Account, loginPort, worldPort, session.Cancellation));
                    return;
                }
                if (header[1] is not (1 or 2)) return;
                if (!AllowAttempt(ip) || !passwordWork.Wait(0)) {
                    await tls.WriteAsync(new byte[]{2}, ct);
                    return;
                }
                try {
                    string login = await ReadText(tls, 24, ct);
                    string password = await ReadText(tls, 512, ct);
                    if (header[1] == 1) {
                        await tls.WriteAsync(new byte[]{database.Register(login, password) ? (byte)0 : (byte)1}, ct);
                    } else await tls.WriteAsync(Login(login,password,loopback), ct);
                } finally { passwordWork.Release(); }
            }
        } catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
            OperationCanceledException or ObjectDisposedException or InvalidDataException or DecoderFallbackException or Microsoft.Data.Sqlite.SqliteException) {
            if (ex is Microsoft.Data.Sqlite.SqliteException) Console.Error.WriteLine("Secure gateway database error.");
        } finally { connections.Release(); }
    }

    static async Task<string> ReadText(Stream stream, int maximum, CancellationToken ct)
    {
        byte[] size = new byte[2];
        await stream.ReadExactlyAsync(size, ct);
        int length = BinaryPrimitives.ReadUInt16BigEndian(size);
        if (length == 0 || length > maximum) throw new InvalidDataException("Invalid credential length");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        try { return StrictUtf8.GetString(data); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
