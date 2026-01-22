using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Helper for generating and managing self-signed certificates for HTTPS.
    /// </summary>
    public static class HttpsHelper
    {
        private static readonly string CertDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kinectv1", "certs");

        private static readonly string CertPath = Path.Combine(CertDir, "webrtc.pfx");
        private const string CertPassword = "kinectv1-webrtc";

        private static string RepoCertDir
        {
            get
            {
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    // Check base dir and a few parents to handle bin output folders
                    var di = new DirectoryInfo(baseDir);
                    for (int i = 0; i < 6 && di != null; i++)
                    {
                        var candidate = Path.Combine(di.FullName, "certs");
                        if (Directory.Exists(candidate)) return candidate;
                        candidate = Path.Combine(di.FullName, "cert");
                        if (Directory.Exists(candidate)) return candidate;
                        di = di.Parent;
                    }
                }
                catch { }
                return null;
            }
        }

        /// <summary>
        /// Get or create a self-signed certificate for HTTPS.
        /// Certificate is valid for 1 year and includes the local machine name and IP.
        /// </summary>
        public static X509Certificate2 GetOrCreateCertificate()
        {
            // 1) Prefer user-provided certificate in repo-local cert(s) folder
            var fromRepo = TryLoadUserCertificate();
            if (fromRepo != null)
            {
                EnsureCertificateInStore(fromRepo);
                Console.WriteLine($"[HTTPS] Using user certificate (expires {fromRepo.NotAfter:yyyy-MM-dd})");
                return fromRepo;
            }

            // 2) Fallback to the generated self-signed PFX in LocalAppData
            if (File.Exists(CertPath))
            {
                try
                {
                    var cert = new X509Certificate2(
                        CertPath,
                        CertPassword,
                        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

                    if (cert.NotAfter > DateTime.Now.AddDays(30))
                    {
                        EnsureCertificateInStore(cert);
                        Console.WriteLine($"[HTTPS] Using existing certificate (expires {cert.NotAfter:yyyy-MM-dd})");
                        return cert;
                    }

                    Console.WriteLine("[HTTPS] Certificate expiring soon; regenerating");
                    cert.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HTTPS] Failed to load existing cert: {ex.Message}");
                }
            }

            // Generate new certificate
            return GenerateCertificate();
        }

        private static X509Certificate2 TryLoadUserCertificate()
        {
            try
            {
                var dir = RepoCertDir;
                if (string.IsNullOrWhiteSpace(dir)) return null;

                // Allow either PFX or PEM pair.
                // PFX search (prefer explicit webrtc.pfx)
                var pfx = Path.Combine(dir, "webrtc.pfx");
                if (!File.Exists(pfx))
                {
                    var pfxs = Directory.GetFiles(dir, "*.pfx");
                    if (pfxs.Length > 0) pfx = pfxs[0];
                }

                if (File.Exists(pfx))
                {
                    // If user provided a password-protected PFX, they can set CERT_PFX_PASSWORD env var.
                    var pwd = Environment.GetEnvironmentVariable("KINECTV1_PFX_PASSWORD") ?? string.Empty;
                    var cert = new X509Certificate2(
                        pfx,
                        pwd,
                        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
                    return cert;
                }

                // PEM search: try to find a cert and a matching key.
                // Common patterns:
                //  - *.pem + *-key.pem
                //  - cert.pem + key.pem
                var certPem = FindFirstExisting(dir, new[] { "cert.pem", "certificate.pem" });
                var keyPem = FindFirstExisting(dir, new[] { "key.pem", "private.key", "privkey.pem" });

                if (certPem == null)
                {
                    var pemCandidates = Directory.GetFiles(dir, "*.pem");
                    foreach (var c in pemCandidates)
                    {
                        if (c.EndsWith("-key.pem", StringComparison.OrdinalIgnoreCase)) continue;
                        certPem = c;
                        break;
                    }
                }

                if (keyPem == null)
                {
                    var keyCandidates = Directory.GetFiles(dir, "*-key.pem");
                    if (keyCandidates.Length > 0) keyPem = keyCandidates[0];
                }

                if (certPem != null && keyPem != null)
                {
                    var cert = X509Certificate2.CreateFromPemFile(certPem, keyPem);
                    // Ensure we have a PFX-backed cert with key persisted for http.sys/netsh.
                    var exported = new X509Certificate2(
                        cert.Export(X509ContentType.Pfx),
                        (string)null,
                        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
                    return exported;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTPS] User cert load failed: {ex.Message}");
            }

            return null;
        }

        private static string FindFirstExisting(string dir, string[] names)
        {
            foreach (var n in names)
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>
        /// Ensure the certificate is installed in the Local Machine store so netsh can use it.
        /// </summary>
        private static void EnsureCertificateInStore(X509Certificate2 cert)
        {
            try
            {
                using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
                    if (found.Count == 0)
                    {
                        store.Add(cert);
                    }
                }

                try
                {
                    using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                    {
                        store.Open(OpenFlags.ReadWrite);
                        var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
                        if (found.Count == 0)
                        {
                            store.Add(cert);
                        }
                    }
                }
                catch (CryptographicException)
                {
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTPS] Warning: Could not add cert to store: {ex.Message}");
            }
        }

        private static X509Certificate2 GenerateCertificate()
        {
            Console.WriteLine("[HTTPS] Generating new self-signed certificate...");

            Directory.CreateDirectory(CertDir);

            // Get local hostname and IPs for SAN
            var hostName = Dns.GetHostName();
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(hostName);
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

            // Add all local IPs
            try
            {
                var entry = Dns.GetHostEntry(hostName);
                foreach (var ip in entry.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        sanBuilder.AddIpAddress(ip);
                    }
                }
            }
            catch { }

            // Generate RSA key
            using var rsa = RSA.Create(2048);

            // Build certificate request
            var request = new CertificateRequest(
                $"CN={hostName}, O=Kinectv1 WebRTC, OU=Self-Signed",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // Add extensions
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    false));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    false));

            request.CertificateExtensions.Add(sanBuilder.Build());

            // Create self-signed certificate (valid for 1 year)
            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = DateTimeOffset.UtcNow.AddYears(1);

            using var cert = request.CreateSelfSigned(notBefore, notAfter);

            // Export with private key - use MachineKeySet for netsh compatibility
            var certWithKey = new X509Certificate2(
                cert.Export(X509ContentType.Pfx, CertPassword),
                CertPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

            // Save to file
            var pfxBytes = certWithKey.Export(X509ContentType.Pfx, CertPassword);
            File.WriteAllBytes(CertPath, pfxBytes);

            // Install to certificate store
            EnsureCertificateInStore(certWithKey);

            Console.WriteLine($"[HTTPS] Generated certificate '{hostName}' valid until {certWithKey.NotAfter:yyyy-MM-dd}");

            return certWithKey;
        }

        /// <summary>
        /// Get the path to the certificate file (for display/export purposes).
        /// </summary>
        public static string GetCertificatePath() => CertPath;

        /// <summary>
        /// Delete the existing certificate (forces regeneration on next use).
        /// Also removes from certificate stores.
        /// </summary>
        public static void DeleteCertificate()
        {
            // Try to remove from stores first
            try
            {
                if (File.Exists(CertPath))
                {
                    var cert = new X509Certificate2(CertPath, CertPassword);
                    var thumbprint = cert.Thumbprint;
                    cert.Dispose();

                    // Remove from CurrentUser store
                    try
                    {
                        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                        store.Open(OpenFlags.ReadWrite);
                        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
                        foreach (var c in found)
                        {
                            store.Remove(c);
                        }
                    }
                    catch { }

                    // Remove from LocalMachine store
                    try
                    {
                        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                        store.Open(OpenFlags.ReadWrite);
                        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
                        foreach (var c in found)
                        {
                            store.Remove(c);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Delete the file
            if (File.Exists(CertPath))
            {
                File.Delete(CertPath);
                Console.WriteLine("[HTTPS] Certificate deleted");
            }
        }
    }
}
