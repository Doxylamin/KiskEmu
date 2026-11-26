using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aion.Host.Platform;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>Official config/API host names served by the platform bootstrap over the NCGuard TLS port (Aion:Platform:TlsApiHost).</summary>
public sealed class PlatformTlsHostTests
{
    const string Key = "0123456789abcdef";
    const int TlsPort = 443;

    static JsonObject ServedPolicy(string? tlsApiHost, string? code = null)
    {
        var dir = Directory.CreateTempSubdirectory("aion-policy-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "app-key.json"), $$"""{"value":"{{Convert.ToBase64String(new byte[256])}}"}""");
            File.WriteAllText(Path.Combine(dir.FullName, "local-test-key.json"), $$"""{"key":"{{Key}}"}""");
            var plain = Path.Combine(dir.FullName, "policy.json");
            File.WriteAllText(plain, """{"api_gate":"http://127.0.0.1:8087/local-api-gate","login_web":"http://127.0.0.1:8087/local-login-web","custom_data":{}}""");
            var responses = PlatformResponses.Load(new PlatformOptions { FixtureDirectory = dir.FullName, PolicyPlaintextFile = plain, TlsApiHost = tlsApiHost }, 3700, 3701);
            var cipher = Convert.FromBase64String(JsonNode.Parse(responses.PolicyFor(code)!)!["value"]!.GetValue<string>());
            using var aes = Aes.Create();
            aes.Key = Encoding.ASCII.GetBytes(Key);
            return JsonNode.Parse(aes.DecryptCbc(cipher, aes.Key, PaddingMode.PKCS7))!.AsObject();
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Policy_stays_on_the_http_bootstrap_by_default()
    {
        Assert.Equal("http://127.0.0.1:8087/local-api-gate", ServedPolicy(null)["api_gate"]!.GetValue<string>());
    }

    [Fact]
    public void Tls_api_host_points_the_code_less_policy_at_https()
    {
        var policy = ServedPolicy("api.g.nc.com");
        Assert.Equal("https://api.g.nc.com/local-api-gate", policy["api_gate"]!.GetValue<string>());
        Assert.Equal("https://api.g.nc.com/local-login-web", policy["login_web"]!.GetValue<string>());
    }

    [Fact]
    public void Launch_code_policy_keeps_the_http_form()
    {
        Assert.Equal("http://127.0.0.1:8087/c/abc/local-api-gate", ServedPolicy("api.g.nc.com", "abc")["api_gate"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/virtual_currency/v2.0/me/balances", "/local-api-gate/virtual_currency/v2.0/me/balances")]
    [InlineData("/goods/v2.0/goods", "/local-api-gate/goods/v2.0/goods")]
    [InlineData("/local-api-gate/virtual_currency/v2.0/me/balances", "/local-api-gate/virtual_currency/v2.0/me/balances")]
    [InlineData("/MobileAppConfigSdk/app/policy", "/MobileAppConfigSdk/app/policy")]
    [InlineData("/local-login-web/x", "/local-login-web/x")]
    public void Api_gate_host_paths_map_onto_the_local_api_gate_routes(string path, string expected)
    {
        Assert.Equal(expected, PlatformBootstrapEndpoints.ConfigHostPath("api.g.nc.com", path));
    }

    [Fact]
    public void Config_host_paths_are_kept()
    {
        Assert.Equal("/MobileAppConfigSdk/app/key", PlatformBootstrapEndpoints.ConfigHostPath("mobileappconfig.g.nc.com", "/MobileAppConfigSdk/app/key"));
    }

    sealed class Tls(string? host) : ITlsHandshakeFeature
    {
        public string HostName => host ?? "";
        public System.Security.Authentication.SslProtocols Protocol => default;
        public System.Net.Security.TlsCipherSuite? NegotiatedCipherSuite => null;
#pragma warning disable SYSLIB0058
        public System.Security.Authentication.CipherAlgorithmType CipherAlgorithm => default;
        public int CipherStrength => 0;
        public System.Security.Authentication.HashAlgorithmType HashAlgorithm => default;
        public int HashStrength => 0;
        public System.Security.Authentication.ExchangeAlgorithmType KeyExchangeAlgorithm => default;
        public int KeyExchangeStrength => 0;
#pragma warning restore SYSLIB0058
    }

    static DefaultHttpContext Request(int localPort, string? sni)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = localPort;
        context.Features.Set<ITlsHandshakeFeature>(new Tls(sni));
        return context;
    }

    [Fact]
    public void Only_official_config_hosts_on_the_tls_port_are_diverted()
    {
        Assert.Equal("api.g.nc.com", PlatformBootstrapEndpoints.ConfigHostOf(Request(TlsPort, "API.G.NC.COM"), TlsPort)?.ToLowerInvariant());
        Assert.Null(PlatformBootstrapEndpoints.ConfigHostOf(Request(TlsPort, "bladnoch.nc.com"), TlsPort));
        Assert.Null(PlatformBootstrapEndpoints.ConfigHostOf(Request(8087, "api.g.nc.com"), TlsPort));
        Assert.Null(PlatformBootstrapEndpoints.ConfigHostOf(Request(TlsPort, "api.g.nc.com"), null));
    }
}
