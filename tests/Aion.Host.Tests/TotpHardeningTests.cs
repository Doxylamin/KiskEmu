using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Aion.Application.Authentication;
using Aion.Core.Accounts;
using Aion.Core.Security;
using Aion.Host.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>
/// Two-step sign-in hardening (forge #72, after addiboii/A2Core): RFC 6238 vectors, a code accepted once, constant-time
/// matching over the whole window, recovery-code normalisation, the dedicated secret key with its BridgeSecret fallback,
/// and the password step before enrollment. The Postgres side (last_step, migration 109) needs a database and is not run here.
/// </summary>
public sealed class TotpHardeningTests : IAsyncDisposable
{
    static readonly string RfcSecret = Totp.Base32Encode("12345678901234567890"u8);

    [Fact]
    public void RfcSecretEncodesToTheKnownBase32() =>
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", RfcSecret);

    // RFC 6238 appendix B, SHA-1 rows: the 8-digit values truncated to the 6 digits authenticator apps show.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Rfc6238Vectors(long unixSeconds, string expected)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(expected, Totp.CodeForStep(RfcSecret, Totp.CurrentStep(at)));
        Assert.Equal(Totp.CurrentStep(at), Totp.Match(RfcSecret, expected, at));
    }

    [Fact]
    public void ACodeIsAcceptedOnce()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var step = Totp.CurrentStep(at);
        Assert.Equal(step, Totp.Match(RfcSecret, "081804", at));
        // The caller stores the step; the same code is refused afterwards, and so is an older one inside the window.
        Assert.Null(Totp.Match(RfcSecret, "081804", at, afterStep: step));
        Assert.Null(Totp.Match(RfcSecret, Totp.CodeForStep(RfcSecret, step - 1), at, afterStep: step));
        // The next step's code (clock drift) is still accepted.
        Assert.Equal(step + 1, Totp.Match(RfcSecret, Totp.CodeForStep(RfcSecret, step + 1), at, afterStep: step));
    }

    [Fact]
    public void OneStepOfDriftEitherSide()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var step = Totp.CurrentStep(at);
        Assert.Equal(step - 1, Totp.Match(RfcSecret, Totp.CodeForStep(RfcSecret, step - 1), at));
        Assert.Equal(step + 1, Totp.Match(RfcSecret, Totp.CodeForStep(RfcSecret, step + 1), at));
        Assert.Null(Totp.Match(RfcSecret, Totp.CodeForStep(RfcSecret, step + 2), at));
        Assert.Null(Totp.Match(RfcSecret, Totp.CodeForStep(RfcSecret, step - 2), at));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("08180x")]
    [InlineData("0818040")]
    [InlineData("08180")]
    [InlineData("０８１８０４")]  // full-width digits are not ASCII digits
    public void MalformedCodesAreRefused(string? code) =>
        Assert.Null(Totp.Match(RfcSecret, code, DateTimeOffset.FromUnixTimeSeconds(1111111109)));

    [Fact]
    public void SpacesInACodeAreIgnored() =>
        Assert.NotNull(Totp.Match(RfcSecret, " 081 804 ", DateTimeOffset.FromUnixTimeSeconds(1111111109)));

    [Theory]
    [InlineData("ABCDE12345")]
    [InlineData("abcde12345")]
    [InlineData("abcde-12345")]
    [InlineData(" AbCdE 12345 ")]
    [InlineData("ab\tcd e1–2345")]  // tab, non-breaking space, en dash
    [InlineData("ABCDE−12345")]           // minus sign
    public void RecoveryCodesAreNormalized(string typed) =>
        Assert.Equal("ABCDE12345", RecoveryCodes.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDE1234")]
    [InlineData("ABCDE123456")]
    [InlineData("ABCDG12345")]
    [InlineData("ABCDE_12345")]
    public void NonCodesAreRefused(string? typed) => Assert.Null(RecoveryCodes.Normalize(typed));

    [Fact]
    public void OverlongRecoveryInputIsRefusedUnread() =>
        Assert.Null(RecoveryCodes.Normalize("ABCDE12345" + new string(' ', RecoveryCodes.MaxInput)));

    [Fact]
    public void RecoveryCodesIssuedEarlierStillMatch()
    {
        // The stored form before this change: upper-case hex SHA-256 of the 10 upper-case hex digits.
        var stored = new[] { "0000000000", "ABCDE12345", "FFFFFFFFFF" }
            .Select(c => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c)))).ToArray();
        Assert.Equal(1, RecoveryCodes.Find(stored, "abcde-12345"));
        Assert.Equal(2, RecoveryCodes.Find(stored, "ffff fffff f"));
        Assert.Equal(-1, RecoveryCodes.Find(stored, "1234512345"));
        Assert.Equal(-1, RecoveryCodes.Find(stored, null));
    }

    [Fact]
    public void GeneratedRecoveryCodesMatchTheirHashes()
    {
        var (plain, hashed) = RecoveryCodes.GenerateSet();
        Assert.Equal(RecoveryCodes.Count, plain.Length);
        for (var i = 0; i < plain.Length; i++)
        {
            Assert.Matches("^[0-9A-F]{10}$", plain[i]);
            Assert.Equal(i, RecoveryCodes.Find(hashed, plain[i].ToLowerInvariant()));
        }
    }

    static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    static readonly string Bridge = new('b', 64);

    [Fact]
    public void NoKeyAndNoBridgeSecretMeansNoTwoStep()
    {
        Assert.Null(TotpSecretProtection.FromConfiguration(null, null, out var warning));
        Assert.Null(warning);
        Assert.Null(TotpSecretProtection.FromConfiguration("", "too short", out _));
    }

    [Fact]
    public void ADedicatedKeyMustBe32Bytes()
    {
        Assert.Throws<InvalidOperationException>(() => TotpSecretProtection.FromConfiguration(Convert.ToBase64String(new byte[16]), null, out _));
        Assert.Throws<InvalidOperationException>(() => TotpSecretProtection.FromConfiguration("not base64!", null, out _));
    }

    [Fact]
    public void WithoutADedicatedKeyTheBridgeDerivedKeyKeepsWorkingWithAWarning()
    {
        var legacy = TotpSecretProtection.FromConfiguration(null, Bridge, out var warning)!;
        Assert.True(legacy.UsesBridgeDerivedKey);
        Assert.Contains("Aion:Totp:EncryptionKey", warning);
        // Same key as the old Program.cs derivation: base64(SHA-256(BridgeSecret)).
        var old = new TotpSecretProtection(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Bridge))));
        var (cipher, nonce) = old.Encrypt(RfcSecret);
        Assert.Equal(RfcSecret, legacy.Decrypt(cipher, nonce, out var isLegacy));
        Assert.False(isLegacy);
    }

    [Fact]
    public void ADedicatedKeyStillOpensSecretsStoredUnderTheBridgeDerivedKey()
    {
        var (oldCipher, oldNonce) = TotpSecretProtection.FromConfiguration(null, Bridge, out _)!.Encrypt(RfcSecret);
        var keys = TotpSecretProtection.FromConfiguration(NewKey(), Bridge, out var warning)!;
        Assert.Null(warning);
        Assert.False(keys.UsesBridgeDerivedKey);
        Assert.Equal(RfcSecret, keys.Decrypt(oldCipher, oldNonce, out var isLegacy));
        Assert.True(isLegacy);   // the caller encrypts it again under the dedicated key
        var (cipher, nonce) = keys.Encrypt(RfcSecret);
        Assert.Equal(RfcSecret, keys.Decrypt(cipher, nonce, out isLegacy));
        Assert.False(isLegacy);
        // New secrets are not readable with the bridge-derived key: the key is separate.
        Assert.Null(TotpSecretProtection.FromConfiguration(null, Bridge, out _)!.Decrypt(cipher, nonce));
        // Without the bridge secret configured, the old ciphertext cannot be read.
        Assert.Null(TotpSecretProtection.FromConfiguration(NewKey(), null, out _)!.Decrypt(oldCipher, oldNonce));
    }

    [Fact]
    public void ATamperedSecretIsRefused()
    {
        var keys = TotpSecretProtection.FromConfiguration(NewKey(), null, out _)!;
        var (cipher, nonce) = keys.Encrypt(RfcSecret);
        cipher[0] ^= 1;
        Assert.Null(keys.Decrypt(cipher, nonce));
    }

    // --- POST /api/accounts/totp/enroll needs the password -------------------------------------------------------------

    const string SessionToken = "player-session";
    readonly FakeTotp totp = new();
    WebApplication? app;
    HttpClient? http;

    async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<ITotpAuthentication>(totp);
        builder.Services.AddSingleton<IAccountAuthentication>(totp);
        builder.Services.AddRateLimiter(options =>
        {
            foreach (var policy in new[] { "account-entry", "totp-verify" })
                options.AddPolicy(policy, _ => RateLimitPartition.GetNoLimiter("test"));
        });
        app = builder.Build();
        app.UseRateLimiter();
        app.MapTotpEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return http = new HttpClient { BaseAddress = new Uri(address) };
    }

    static HttpRequestMessage Enroll(object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/totp/enroll");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
        request.Content = body is null ? new StringContent("", Encoding.UTF8, "application/json") : JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task EnrollWithoutThePasswordIsRefused()
    {
        var client = await StartAsync();
        foreach (var body in new object?[] { null, new { }, new { password = "" } })
        {
            var response = await client.SendAsync(Enroll(body));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("password_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        var wrong = await client.SendAsync(Enroll(new { password = "wrong password" }));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("wrong_password", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(0, totp.Enrollments);
        var ok = await client.SendAsync(Enroll(new { password = FakeTotp.Password }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, totp.Enrollments);
    }

    [Fact]
    public async Task EnrollWithoutASessionIsUnauthorized()
    {
        var client = await StartAsync();
        var response = await client.PostAsJsonAsync("/api/accounts/totp/enroll", new { password = FakeTotp.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EnrollWhenAlreadyOnIsAConflict()
    {
        totp.Enabled = true;
        var client = await StartAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Enroll(new { password = FakeTotp.Password }))).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    }

    sealed class FakeTotp : ITotpAuthentication, IAccountAuthentication
    {
        public const string Password = "correct horse";
        static readonly Guid Account = Guid.NewGuid();
        public bool Enabled;
        public int Enrollments;

        public Task<TotpEnrollment?> BeginEnrollmentAsync(Guid accountId, string accountLabel, string password, CancellationToken ct)
        {
            if (Enabled || password != Password) return Task.FromResult<TotpEnrollment?>(null);
            Enrollments++;
            return Task.FromResult<TotpEnrollment?>(new("SECRET", "otpauth://totp/x?secret=SECRET", []));
        }

        public Task<bool> IsEnabledAsync(Guid accountId, CancellationToken ct) => Task.FromResult(Enabled);
        public Task<TotpStatus?> StatusAsync(Guid accountId, CancellationToken ct) => Task.FromResult<TotpStatus?>(new(Enabled, 0));
        public Task<(bool Ok, string[]? RecoveryCodes)> ConfirmEnrollmentAsync(Guid accountId, string code, CancellationToken ct) => Task.FromResult((false, (string[]?)null));
        public Task<(bool Ok, string[]? RecoveryCodes)> RegenerateRecoveryCodesAsync(Guid accountId, string password, CancellationToken ct) => Task.FromResult((false, (string[]?)null));
        public Task<bool> DisableAsync(Guid accountId, string password, string? code, string? recoveryCode, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> VerifyForAccountAsync(Guid accountId, string? code, string? recoveryCode, CancellationToken ct) => Task.FromResult(false);
        public Task<TotpLoginChallenge?> BeginLoginAsync(Guid accountId, TimeSpan? sessionLifetime, CancellationToken ct) => Task.FromResult<TotpLoginChallenge?>(null);
        public Task<AccountSession?> CompleteLoginAsync(string challengeToken, string? code, string? recoveryCode, CancellationToken ct) => Task.FromResult<AccountSession?>(null);
        public Task<bool> AdminResetAsync(Guid targetAccountId, CancellationToken ct) => Task.FromResult(false);

        public Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken ct) =>
            Task.FromResult(token == SessionToken ? new VerifiedAccount(Account) : null);
        public Task SignOutAsync(string token, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> RegisterAsync(string username, string password, CancellationToken ct) => Task.FromResult(false);
        public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken ct) => Task.FromResult<AccountSession?>(null);
        public Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken ct) => Task.FromResult<GameTicket?>(null);
    }
}
