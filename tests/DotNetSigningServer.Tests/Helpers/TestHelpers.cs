using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DotNetSigningServer.Tests.Helpers;

public static class TestHelpers
{
    /// <summary>
    /// Creates a minimal blank A4 PDF and returns it as a base64 string.
    /// </summary>
    public static string CreateMinimalPdfBase64()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        var pdfDoc = new PdfDocument(writer);
        pdfDoc.AddNewPage(PageSize.A4);
        pdfDoc.Close();
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>
    /// Creates a self-signed RSA 2048 certificate suitable for PDF signing tests.
    /// Returns (PEM certificate string, PFX base64 content, PFX password).
    /// </summary>
    public static (string CertificatePem, string PfxBase64, string PfxPassword) CreateTestCertificate()
    {
        const string password = "test-password";

        var keyPairGen = new RsaKeyPairGenerator();
        keyPairGen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        AsymmetricCipherKeyPair keyPair = keyPairGen.GenerateKeyPair();

        var certGen = new X509V3CertificateGenerator();
        var dn = new X509Name("CN=Test Signer, O=Unit Tests");
        certGen.SetSerialNumber(BigInteger.ProbablePrime(120, new Random()));
        certGen.SetIssuerDN(dn);
        certGen.SetSubjectDN(dn);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keyPair.Public);

        var signatureFactory = new Asn1SignatureFactory("SHA256WithRSA", keyPair.Private);
        Org.BouncyCastle.X509.X509Certificate cert = certGen.Generate(signatureFactory);

        // Generate PEM
        string pem;
        using (var sw = new StringWriter())
        {
            var pemWriter = new PemWriter(sw);
            pemWriter.WriteObject(cert);
            pemWriter.Writer.Flush();
            pem = sw.ToString();
        }

        // Generate PFX
        var store = new Pkcs12StoreBuilder().Build();
        var certEntry = new X509CertificateEntry(cert);
        store.SetKeyEntry("key", new AsymmetricKeyEntry(keyPair.Private), new[] { certEntry });

        byte[] pfxBytes;
        using (var pfxMs = new MemoryStream())
        {
            store.Save(pfxMs, password.ToCharArray(), new SecureRandom());
            pfxBytes = pfxMs.ToArray();
        }

        return (pem, Convert.ToBase64String(pfxBytes), password);
    }

    /// <summary>
    /// Creates a fresh InMemory EF Core ApplicationDbContext with a unique database name.
    /// </summary>
    public static ApplicationDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var context = new ApplicationDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    /// <summary>
    /// Wraps a value in IOptions&lt;T&gt; for dependency injection in tests.
    /// </summary>
    public static IOptions<T> WrapOptions<T>(T value) where T : class
    {
        return Microsoft.Extensions.Options.Options.Create(value);
    }

    /// <summary>
    /// Creates a User entity with sensible defaults for testing.
    /// </summary>
    public static User CreateTestUser(string email = "test@example.com")
    {
        var authService = new DotNetSigningServer.Services.AuthService();
        var (hash, salt) = authService.HashPassword("test-password");
        return new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = hash,
            PasswordSalt = salt,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Creates a base64 string whose decoded byte size is approximately the target size.
    /// </summary>
    public static string CreateBase64OfSize(long targetBytes)
    {
        var bytes = new byte[targetBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Creates a mock IWebHostEnvironment configured as the specified environment.
    /// </summary>
    public static IWebHostEnvironment CreateMockEnvironment(string environmentName = "Development")
    {
        var mock = new Mock<IWebHostEnvironment>();
        mock.Setup(e => e.EnvironmentName).Returns(environmentName);
        return mock.Object;
    }

    /// <summary>
    /// Creates a TokenService for tests. Uses Development environment by default.
    /// </summary>
    public static TokenService CreateTokenService(
        string secret = "a-strong-test-secret-for-unit-tests",
        string environmentName = "Development")
    {
        var options = WrapOptions(new DotNetSigningServer.Options.TokenOptions { Secret = secret });
        var env = CreateMockEnvironment(environmentName);
        var logger = NullLogger<TokenService>.Instance;
        return new TokenService(options, env, logger);
    }
}
