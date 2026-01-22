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
        private const string CertPassword = "kinectv1-webrtc"; // Simple password for local use
        
        /// <summary>
        /// Get or create a self-signed certificate for HTTPS.
        /// Certificate is valid for 1 year and includes the local machine name and IP.
        /// </summary>
        public static X509Certificate2 GetOrCreateCertificate()
        {
            // Try to load existing certificate
            if (File.Exists(CertPath))
            {
                try
                {
                    // Load with MachineKeySet to ensure the key is accessible for netsh
                    var cert = new X509Certificate2(
                        CertPath, 
                        CertPassword, 
                        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
                    
                    // Check if still valid (with 30 day buffer)
                    if (cert.NotAfter > DateTime.Now.AddDays(30))
                    {
                        // Ensure cert is in the store for netsh to find it
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

        /// <summary>
        /// Ensure the certificate is installed in the Local Machine store so netsh can use it.
        /// </summary>
        private static void EnsureCertificateInStore(X509Certificate2 cert)
        {
            try
            {
                // Try Personal store first (for the user)
                using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);

                    // Check if already installed
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
                    if (found.Count == 0)
                    {
                        store.Add(cert);
                    }
                }

                // Also try LocalMachine store if we have admin rights
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
                    // No admin rights, that's fine - CurrentUser store should work
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
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
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
