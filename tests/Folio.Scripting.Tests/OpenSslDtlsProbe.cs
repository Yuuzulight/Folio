using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

internal static class OpenSslDtlsProbe
{
    public static int Run(string openssl, bool folioServer = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "folio-openssl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=FolioInterop", signer, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var transport = new DtlsTransport { IsClient = !folioServer, RemoteFingerprint = string.Join(":", SHA256.HashData(certificate.RawData).Select(value => value.ToString("X2"))) };
        var clientCertificate = (X509Certificate2)typeof(DtlsTransport).GetField("_localCert", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transport)!;
        File.WriteAllText(Path.Combine(root, "server.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(root, "server-key.pem"), signer.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(root, "client.pem"), clientCertificate.ExportCertificatePem());
        using var portReservation = new UdpClient(0);
        using var socket = new UdpClient(0);
        var port = ((IPEndPoint)(folioServer ? socket : portReservation).Client.LocalEndPoint!).Port;
        portReservation.Close();
        var start = new ProcessStartInfo(openssl) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        var arguments = new List<string> { folioServer ? "s_client" : "s_server", "-dtls1_2", folioServer ? "-connect" : "-accept", "127.0.0.1:" + port,
            "-cert", Path.Combine(root, "server.pem"), "-key", Path.Combine(root, "server-key.pem"),
            "-CAfile", Path.Combine(root, "client.pem"), folioServer ? "-verify" : "-Verify", "1", "-verify_return_error",
            "-cipher", "ECDHE-ECDSA-AES128-GCM-SHA256", "-sigalgs", "ECDSA+SHA256", "-groups", "P-256", "-use_srtp", "SRTP_AEAD_AES_128_GCM", "-quiet", "-state", "-msg" };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var server = Process.Start(start) ?? throw new InvalidOperationException("OpenSSL did not start.");
        const string fromOpenSsl = "openssl-to-folio-probe";
        const string fromFolio = "folio-to-openssl-probe";
        var errors = server.StandardError.ReadToEndAsync();
        var peerReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = CaptureOutputAsync(server.StandardOutput, fromFolio, peerReceived);
        // Queue input before the handshake; OpenSSL's Windows console input
        // loop otherwise blocks on the redirected pipe before reading UDP.
        server.StandardInput.WriteLine(fromOpenSsl);
        server.StandardInput.Flush();
        var received = new StringBuilder();
        transport.OnApplicationDataReceived += bytes => received.Append(Encoding.UTF8.GetString(bytes));
        socket.Client.ReceiveTimeout = 1000;
        IPEndPoint? destination = folioServer ? null : new IPEndPoint(IPAddress.Loopback, port);
        transport.OnDatagramToSend += bytes =>
        {
            Console.WriteLine($"Sending {bytes.Length} bytes to OpenSSL port {port}.");
            socket.Send(bytes, destination ?? throw new InvalidOperationException("No probe peer."));
        };
        try
        {
            Thread.Sleep(1000);
            transport.StartHandshake();
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(10) && transport.State != "connected" && !server.HasExited)
            {
                IPEndPoint remote = new(IPAddress.Any, 0);
                try
                {
                    var record = socket.Receive(ref remote);
                    if (folioServer && destination is null && IPAddress.IsLoopback(remote.Address)) destination = remote;
                    if (!remote.Equals(destination)) continue;
                    Console.WriteLine($"Received {record.Length} bytes from OpenSSL, type {record[0]}.");
                    transport.ProcessIncomingDatagram(record);
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { }
            }
            Console.WriteLine("Folio DTLS state: " + transport.State);
            if (transport.State != "connected") return 1;
            transport.SendApplicationData(Encoding.UTF8.GetBytes(fromFolio + "\n"));
            deadline.Restart();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5) &&
                (!received.ToString().Contains(fromOpenSsl, StringComparison.Ordinal) || !peerReceived.Task.IsCompletedSuccessfully))
            {
                IPEndPoint remote = new(IPAddress.Any, 0);
                try
                {
                    var record = socket.Receive(ref remote);
                    if (remote.Equals(destination)) transport.ProcessIncomingDatagram(record);
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { }
            }
            // Avoid sending EOF through s_server's Windows console adapter:
            // it can attempt a zero-length SSL_write and emit a spurious error.
            if (!server.HasExited) { server.Kill(); server.WaitForExit(); }
            var outgoingDelivered = output.GetAwaiter().GetResult().Contains(fromFolio, StringComparison.Ordinal);
            var incomingDelivered = received.ToString().Contains(fromOpenSsl, StringComparison.Ordinal);
            Console.WriteLine($"Application data: Folio to OpenSSL={outgoingDelivered}, OpenSSL to Folio={incomingDelivered}.");
            return outgoingDelivered && incomingDelivered ? 0 : 1;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
        finally
        {
            if (!server.HasExited) { server.Kill(); server.WaitForExit(); }
            File.WriteAllText(Path.Combine(root, "openssl-stderr.txt"), errors.GetAwaiter().GetResult());
            File.WriteAllText(Path.Combine(root, "openssl-stdout.txt"), output.GetAwaiter().GetResult());
            Console.WriteLine(errors.Result);
            Console.WriteLine("Probe evidence: " + root);
            // Retain certificates and logs for diagnosis, but never retain a private key.
            File.Delete(Path.Combine(root, "server-key.pem"));
        }
    }

    private static async Task<string> CaptureOutputAsync(StreamReader reader, string marker, TaskCompletionSource received)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        string suffix = "";
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            output.Append(buffer, 0, count);
            // Preserve a split marker across reads without repeatedly copying
            // the complete handshake trace while the peer is still running.
            var text = suffix + new string(buffer, 0, count);
            if (text.Contains(marker, StringComparison.Ordinal)) received.TrySetResult();
            suffix = text.Length < marker.Length ? text : text[^(marker.Length - 1)..];
        }
        return output.ToString();
    }

}
