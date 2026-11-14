using Aion.Core.Security;

// RFC 6238 appendix B, SHA-1 (6-digit truncation of the 8-digit table values).
var rfcSecret = Totp.Base32Encode("12345678901234567890"u8);
Check(rfcSecret == "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "base32 of the RFC secret");
Check(Totp.CodeForStep(rfcSecret, 59 / 30) == "287082" && Totp.CodeForStep(rfcSecret, 1111111109 / 30) == "081804"
    && Totp.CodeForStep(rfcSecret, 20000000000 / 30) == "353130", "RFC 6238 vectors");
var at = DateTimeOffset.FromUnixTimeSeconds(1111111109);
var step = Totp.CurrentStep(at);
Check(Totp.Match(rfcSecret, "081804", at) == step, "current code matches its step");
Check(Totp.Match(rfcSecret, "081804", at, afterStep: step) is null, "a used step is refused (replay)");

var secret = Totp.GenerateSecret();
Check(Totp.Verify(secret, Totp.CodeForStep(secret, Totp.CurrentStep(DateTimeOffset.UtcNow))), "current step");
Check(!Totp.Verify(secret, "000000", window: 0) || Totp.CodeForStep(secret, Totp.CurrentStep(DateTimeOffset.UtcNow)) == "000000", "reject wrong code");
var uri = Totp.BuildOtpAuthUri("Aion Local", "alice", secret);
Check(uri.Contains("secret=" + secret), "otpauth contains secret");
var protection = new TotpSecretProtection(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
var (cipher, nonce) = protection.Encrypt(secret);
Check(protection.Decrypt(cipher, nonce) == secret, "secret roundtrip");
Check(RecoveryCodes.Normalize("abcde-12345") == "ABCDE12345", "recovery code normalized");

static void Check(bool ok, string name)
{
    if (!ok) throw new Exception("Failed: " + name);
    Console.WriteLine("ok " + name);
}

Console.WriteLine("Totp tests passed.");
