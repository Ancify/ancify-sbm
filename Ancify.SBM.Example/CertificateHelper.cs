using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Ancify.SBM.Shared.Transport.TCP;

namespace Ancify.SBM.Example;

public static class CertificateHelper
{
    /// <summary>
    /// Loads a certificate from a specified file path, or creates a new self-signed certificate if the file does not exist.
    /// </summary>
    /// <param name="certificatePath">Path to the certificate file (PFX format).</param>
    /// <param name="password">Password for the certificate. For development, this can be empty.</param>
    /// <returns>An X509Certificate2 instance.</returns>
    public static X509Certificate2 GetOrCreateCertificate(string certificatePath, string password = "")
    {
        if (File.Exists(certificatePath))
        {
            Console.WriteLine($"Loading existing certificate from: {certificatePath}");
            return new X509Certificate2(certificatePath, password);
        }

        Console.WriteLine("Certificate not found. Generating a new self-signed certificate...");

        using (RSA rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // Add Subject Alternative Name for "localhost"
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            request.CertificateExtensions.Add(sanBuilder.Build());

            // Add basic constraints (indicates that this is not a certificate authority)
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            // Add key usage extension for digital signature
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            // Add enhanced key usage for server authentication (OID 1.3.6.1.5.5.7.3.1)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

            // Create the self-signed certificate (valid for 1 year)
            var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));

            // Export the certificate including the private key in PFX format
            byte[] pfxBytes = certificate.Export(X509ContentType.Pfx, password);
            File.WriteAllBytes(certificatePath, pfxBytes);

            return new X509Certificate2(pfxBytes, password);
        }
    }

    /// <summary>
    /// Creates a new SslConfig for development by loading or generating a certificate.
    /// </summary>
    /// <param name="certificatePath">Path to the certificate file (PFX format).</param>
    /// <param name="password">Password for the certificate, if any.</param>
    /// <returns>A configured SslConfig instance.</returns>
    public static SslConfig CreateDevSslConfig(string certificatePath, string password = "")
    {
        var certificate = GetOrCreateCertificate(certificatePath, password);
        return new SslConfig
        {
            Certificate = certificate,
            SslEnabled = true,
            // For development purposes, you may choose to not reject unauthorized certificates.
            RejectUnauthorized = false
        };
    }
}
