using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Tanks.Net;
using UnityEditor;

namespace Tanks.Editor
{
    /// <summary>
    /// Headless protocol smoke test that runs on UNITY'S runtime (Mono), not .NET —
    /// exactly the class of failure the .NET test suite can't catch (e.g. the Mono BCL
    /// throwing PlatformNotSupportedException from RSA APIs). Steps through each crypto
    /// primitive individually (so a failure names the guilty API), then runs a real
    /// two-Network handshake + teardown over NativeTransport on loopback.
    ///
    /// Run (exit code 0 = pass, 1 = fail; grep the log for "PROTOCOL SMOKE"):
    ///   Unity -batchmode -nographics -projectPath "&lt;repo&gt;" \
    ///     -executeMethod Tanks.Editor.ProtocolSmokeTest.Run -logFile "&lt;log&gt;"
    /// </summary>
    public static class ProtocolSmokeTest
    {
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

        public static void Run()
        {
            try
            {
                Step("keypair generate + PKCS#1 export", () =>
                {
                    using (var keyPair = TanksPeerCrypto.Instance.GenerateKeyPair())
                    {
                        if (keyPair.PublicKey.Length < 256)
                            throw new Exception($"public key suspiciously short: {keyPair.PublicKey.Length}");
                    }
                });

                using (var keyPair = TanksPeerCrypto.Instance.GenerateKeyPair())
                {
                    IPeerVerifier verifier = null;
                    Step("PKCS#1 import (CreateVerifier)", () =>
                    {
                        verifier = TanksPeerCrypto.Instance.CreateVerifier(keyPair.PublicKey);
                    });

                    using (verifier)
                    {
                        Step("sign + verify (SHA-256 / PKCS#1)", () =>
                        {
                            byte[] message = new byte[48];
                            for (int i = 0; i < message.Length; i++) message[i] = (byte)(i * 5);
                            byte[] signature = new byte[TanksPeerCrypto.Instance.SignatureSize];
                            keyPair.Sign(message, signature);
                            if (!verifier.Verify(message, signature))
                                throw new Exception("signature did not verify");
                            message[0] ^= 1;
                            if (verifier.Verify(message, signature))
                                throw new Exception("tampered message verified");
                        });

                        Step("RSA encrypt + decrypt (OAEP-SHA1; -SHA256 unsupported on Mono)", () =>
                        {
                            byte[] nonce = new byte[16];
                            new Random(1234).NextBytes(nonce);
                            byte[] cipher = new byte[256];
                            if (!verifier.TryEncrypt(nonce, cipher, out int cipherLen))
                                throw new Exception("TryEncrypt failed");
                            byte[] plain = new byte[256];
                            if (!keyPair.TryDecrypt(new ReadOnlySpan<byte>(cipher, 0, cipherLen), plain, out int plainLen))
                                throw new Exception("TryDecrypt failed");
                            if (plainLen != nonce.Length)
                                throw new Exception($"decrypted length mismatch: {plainLen}");
                            for (int i = 0; i < nonce.Length; i++)
                                if (plain[i] != nonce[i])
                                    throw new Exception("decrypted bytes mismatch");
                        });
                    }
                }

                Step("KDF derive (TLS PRF / HMAC-SHA256)", () =>
                {
                    byte[] okm = new byte[TanksPeerCrypto.Instance.SessionKeySize];
                    TanksPeerCrypto.Instance.Kdf.Derive(
                        ikm: new byte[16], salt: new byte[16],
                        info: NetworkConstants.SessionKeyInfo, okm: okm);
                });

                Step("full handshake + teardown over NativeTransport (loopback)", RunLoopbackHandshake);

                UnityEngine.Debug.Log("PROTOCOL SMOKE PASSED");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"PROTOCOL SMOKE FAILED: {ex}");
                EditorApplication.Exit(1);
            }
        }

        private static void Step(string name, Action action)
        {
            UnityEngine.Debug.Log($"[smoke] {name}...");
            action();
            UnityEngine.Debug.Log($"[smoke] {name} OK");
        }

        private static void RunLoopbackHandshake()
        {
            var ta = new NativeTransport();
            var tb = new NativeTransport();
            IPAddress loopback = ta.DualMode ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            int portA = Bind(ta, loopback, 48960);
            int portB = Bind(tb, loopback, portA + 1);

            using (ta)
            using (tb)
            using (var a = new Network(ta, TanksPeerCrypto.Instance, NullLogger.Instance, 1, 1, 1 << 10))
            using (var b = new Network(tb, TanksPeerCrypto.Instance, NullLogger.Instance, 1, 1, 1 << 10))
            {
                try
                {
                    a.Start();
                    b.Start();

                    // The manual-discovery flow, minus the IMGUI: id + endpoint as text.
                    if (!PeerId.TryParse(b.LocalPeerId.ToVerboseString(), out PeerId remoteId))
                        throw new Exception("PeerId.TryParse failed");
                    string endpointText = loopback.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                        ? $"[{loopback}]:{portB}"
                        : $"{loopback}:{portB}";
                    if (!EndPointParser.TryParse(endpointText, out IPEndPoint remoteEndPoint, out string error))
                        throw new Exception($"EndPointParser failed: {error}");

                    var discovery = new ManualPeerDiscovery();
                    discovery.Set(remoteId, remoteEndPoint);
                    foreach (PeerDiscoveryResult peer in discovery.GetPeers())
                        a.Connect(peer.PeerId, peer.EndPoint);

                    WaitFor(a, NetworkEventKind.SessionEstablished, "initiator");
                    WaitFor(b, NetworkEventKind.SessionEstablished, "responder");

                    a.Disconnect(b.LocalPeerId);
                    WaitFor(b, NetworkEventKind.SessionClosed, "responder (close)");
                }
                finally
                {
                    a.Stop();
                    b.Stop();
                }
            }
        }

        private static int Bind(NativeTransport transport, IPAddress address, int basePort)
        {
            for (int port = basePort; port < basePort + 50; port++)
            {
                try
                {
                    transport.Bind(NetAddress.FromIPEndPoint(new IPEndPoint(address, port)));
                    return port;
                }
                catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
                {
                }
            }
            throw new Exception($"no free loopback UDP port in {basePort}..{basePort + 49}");
        }

        private static void WaitFor(Network network, NetworkEventKind kind, string who)
        {
            var rx = network.Umem.Rx;
            var sw = Stopwatch.StartNew();
            var handle = default(BufferHandle);
            while (sw.Elapsed < HandshakeTimeout)
            {
                while (rx.TryDequeue(ref handle))
                {
                    ref var metadata = ref rx.Deref<NetworkEventMetadata>(handle);
                    NetworkEventKind seen = metadata.Kind;
                    rx.Return(handle);
                    if (seen == kind)
                        return;
                }
                Thread.Sleep(5);
            }
            throw new Exception($"{who} did not observe {kind} within {HandshakeTimeout.TotalSeconds}s");
        }

        // Discard logger: the smoke asserts outcomes; protocol chatter stays out of the log.
        private sealed class NullLogger : ILog
        {
            public static readonly NullLogger Instance = new NullLogger();
            public bool IsEnabled(LogLevel level) => false;
            public void Log(ref LogMessage message) => message.Return();
            public void LogTrace(string message) { }
            public void LogDebug(string message) { }
            public void LogInformation(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message) { }
        }
    }
}
