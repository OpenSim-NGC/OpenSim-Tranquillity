using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpenSim.Framework.Servers.HttpServer;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Framework;

public class HttpCertificateLoadingTests
{
    [Theory]
    [InlineData(X509ContentType.Pkcs12, "test-password")]
    [InlineData(X509ContentType.Pkcs12, "")]
    [InlineData(X509ContentType.Cert, "")]
    public void LoadsCertificateAndPreservesItsIdentityAndPrivateKey(X509ContentType format, string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        string path = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(path, certificate.Export(format, password));
            var server = new BaseHttpServer(0, true, "localhost", path, password);
            using var loaded = GetCertificate(server);

            Assert.True(server.UseSSL);
            Assert.True(server.CheckSSLCertHost("localhost"));
            Assert.False(server.CheckSSLCertHost("unrelated.example"));
            Assert.Equal(certificate.Thumbprint, loaded.Thumbprint);
            Assert.Equal(format == X509ContentType.Pkcs12, loaded.HasPrivateKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WrongPkcs12PasswordReportsTheLoadingFailure()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        string path = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, "correct"));
            var error = Assert.Throws<Exception>(() => new BaseHttpServer(0, true, path, "wrong"));

            Assert.Equal("SSL cert load error", error.Message);
            Assert.IsAssignableFrom<CryptographicException>(error.InnerException);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static X509Certificate2 GetCertificate(BaseHttpServer server)
    {
        var field = typeof(BaseHttpServer).GetField("m_cert", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<X509Certificate2>(field.GetValue(server));
    }
}
