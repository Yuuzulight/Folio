using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

internal static class OpenSslDtlsProbe
{
    public static int Run(string openssl)
    {
        var root = Path.Combine(Path.GetTempPath(), "folio-openssl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=FolioInterop", signer, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var transport = new DtlsTransport { RemoteFingerprint = string.Join(":", SHA256.HashData(certificate.RawData).Select(value => value.ToString("X2"))) };
        var clientCertificate = (X509Certificate2)typeof(DtlsTransport).GetField("_localCert", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transport)!;
        File.WriteAllText(Path.Combine(root, "server.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(root, "server-key.pem"), signer.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(root, "client.pem"), clientCertificate.ExportCertificatePem());
        using var portReservation = new UdpClient(0);
        var port = ((IPEndPoint)portReservation.Client.LocalEndPoint!).Port;
        portReservation.Close();
        var start = new ProcessStartInfo(openssl) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "s_server", "-dtls1_2", "-accept", "127.0.0.1:" + port,
            "-cert", Path.Combine(root, "server.pem"), "-key", Path.Combine(root, "server-key.pem"),
            "-CAfile", Path.Combine(root, "client.pem"), "-Verify", "1", "-verify_return_error",
            "-cipher", "ECDHE-ECDSA-AES128-GCM-SHA256", "-sigalgs", "ECDSA+SHA256", "-groups", "P-256", "-quiet", "-state", "-msg" })
            start.ArgumentList.Add(argument);
        using var server = Process.Start(start) ?? throw new InvalidOperationException("OpenSSL did not start.");
        var errors = server.StandardError.ReadToEndAsync();
        var output = server.StandardOutput.ReadToEndAsync();
        using var socket = new UdpClient(0);
        socket.Client.ReceiveTimeout = 1000;
        var destination = new IPEndPoint(IPAddress.Loopback, port);
        transport.OnDatagramToSend += bytes =>
        {
            Console.WriteLine($"Sending {bytes.Length} bytes to OpenSSL port {port}.");
            socket.Send(bytes, destination);
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
                    if (!remote.Equals(destination)) continue;
                    Console.WriteLine($"Received {record.Length} bytes from OpenSSL, type {record[0]}.");
                    transport.ProcessIncomingDatagram(record);
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { }
            }
            Console.WriteLine("Folio DTLS state: " + transport.State);
            return transport.State == "connected" ? 0 : 1;
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
}
