using System.Collections.Generic;
using System.IO;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Auth;

namespace NINA.Polaris.Test;

/// <summary>
/// AUTH-1: pins the AuthService contract against the AUTH-2 middleware
/// + frontend that will depend on it. Covers password hashing (salt
/// randomness, PBKDF2 roundtrip), session lifecycle, change-password
/// session invalidation, rate-limit lockout, and the
/// fixed-time-compare path. No HTTP infrastructure here; that's the
/// AUTH-5 integration suite.
///
/// Each test gets an isolated profile dir under TempPath so writes
/// don't pollute the real LocalApplicationData/NINA.Polaris/profiles.
/// </summary>
[TestFixture]
public class AuthServiceTests {

    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void Cleanup() {
        foreach (var d in _tempDirs) {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch { /* best-effort, OS will sweep TempPath eventually */ }
        }
        _tempDirs.Clear();
    }

    private (AuthService auth, ProfileService profiles) Make() {
        var dir = Path.Combine(Path.GetTempPath(),
            "polaris-auth-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Profiles:Directory"] = dir
            })
            .Build();
        var profiles = new ProfileService(cfg,
            NullLogger<ProfileService>.Instance);
        var auth = new AuthService(profiles,
            NullLogger<AuthService>.Instance);
        return (auth, profiles);
    }

    [Test]
    public void IsConfigured_FreshProfile_IsFalse() {
        var (auth, _) = Make();
        Assert.That(auth.IsConfigured, Is.False);
    }

    [Test]
    public void SetInitialPassword_FirstCall_SetsHashAndReturnsToken() {
        var (auth, profiles) = Make();
        var token = auth.SetInitialPassword("hunter2!");
        Assert.That(token, Is.Not.Null);
        Assert.That(auth.IsConfigured, Is.True);
        Assert.That(profiles.Active.AuthPasswordHash, Is.Not.Empty);
        Assert.That(profiles.Active.AuthPasswordSalt, Is.Not.Empty);
        // ValidateToken should accept the returned token immediately.
        Assert.That(auth.ValidateToken(token), Is.True);
    }

    [Test]
    public void SetInitialPassword_SecondCall_ReturnsNull() {
        var (auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var second = auth.SetInitialPassword("rebound!");
        Assert.That(second, Is.Null,
            "Setup must only succeed on a fresh install");
    }

    [Test]
    public void SetInitialPassword_TooShort_Throws() {
        var (auth, _) = Make();
        Assert.That(() => auth.SetInitialPassword("short"),
            Throws.TypeOf<ArgumentException>());
        Assert.That(auth.IsConfigured, Is.False,
            "Failed setup must not partially commit");
    }

    [Test]
    public void HashPassword_TwoCalls_ProduceDifferentHashesAndSalts() {
        // Two fresh profiles. Same password through SetInitialPassword
        // must produce different stored hashes (salt is per-call random).
        var (a, profA) = Make();
        var (b, profB) = Make();
        a.SetInitialPassword("samepwd!");
        b.SetInitialPassword("samepwd!");
        Assert.That(profA.Active.AuthPasswordHash,
            Is.Not.EqualTo(profB.Active.AuthPasswordHash));
        Assert.That(profA.Active.AuthPasswordSalt,
            Is.Not.EqualTo(profB.Active.AuthPasswordSalt));
    }

    [Test]
    public void Login_CorrectPassword_ReturnsToken() {
        var (auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var token = auth.Login("hunter2!", IPAddress.Loopback);
        Assert.That(token, Is.Not.Null);
        Assert.That(auth.ValidateToken(token), Is.True);
    }

    [Test]
    public void Login_WrongPassword_ReturnsNull() {
        var (auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var token = auth.Login("wrong-pwd", IPAddress.Loopback);
        Assert.That(token, Is.Null);
    }

    [Test]
    public void Login_BeforeSetup_ReturnsNull() {
        var (auth, _) = Make();
        var token = auth.Login("anything", IPAddress.Loopback);
        Assert.That(token, Is.Null);
    }

    [Test]
    public void Login_RateLimit_LocksAfterRepeatedFailures() {
        var (auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var ip = IPAddress.Parse("192.168.1.50");
        // 5 in-window failures is fine, 6th triggers lockout. Loop a
        // generous 8 attempts to make sure we cross the threshold even
        // if defaults change a bit.
        for (int i = 0; i < 8; i++) {
            auth.Login("wrong", ip);
        }
        // Now even a CORRECT password is rejected because the IP is
        // locked. Restart of the service is the only way out (or wait
        // the backoff window — too slow for a test).
        var locked = auth.Login("hunter2!", ip);
        Assert.That(locked, Is.Null,
            "Locked-out IP should not be able to authenticate");
        // A different IP is unaffected by another IP's lockout.
        var other = auth.Login("hunter2!", IPAddress.Parse("192.168.1.51"));
        Assert.That(other, Is.Not.Null,
            "Lockout must be per-IP, not global");
    }

    [Test]
    public void Logout_InvalidatesOnlyTheCallerSession() {
        var (auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var a = auth.Login("hunter2!", IPAddress.Loopback);
        var b = auth.Login("hunter2!", IPAddress.Loopback);
        Assert.That(auth.ValidateToken(a), Is.True);
        Assert.That(auth.ValidateToken(b), Is.True);
        auth.Logout(a!);
        Assert.That(auth.ValidateToken(a), Is.False);
        Assert.That(auth.ValidateToken(b), Is.True,
            "Logout must scope to the presented token");
    }

    [Test]
    public void ChangePassword_KeepsCallerSessionDropsOthers() {
        var (auth, _) = Make();
        auth.SetInitialPassword("hunter2!");
        var keep = auth.Login("hunter2!", IPAddress.Loopback)!;
        var other = auth.Login("hunter2!", IPAddress.Loopback)!;
        var renewed = auth.ChangePassword("hunter2!", "newpass!", keep);
        Assert.That(renewed, Is.EqualTo(keep),
            "Caller's session token survives the password change");
        Assert.That(auth.ValidateToken(keep), Is.True);
        Assert.That(auth.ValidateToken(other), Is.False,
            "All other sessions must be invalidated");
        // The new password works for fresh logins; the old one doesn't.
        Assert.That(auth.Login("hunter2!", IPAddress.Loopback), Is.Null);
        Assert.That(auth.Login("newpass!", IPAddress.Loopback),
            Is.Not.Null);
    }

    [Test]
    public void ChangePassword_WrongCurrentPassword_DoesNothing() {
        var (auth, profiles) = Make();
        auth.SetInitialPassword("hunter2!");
        var keep = auth.Login("hunter2!", IPAddress.Loopback)!;
        var originalHash = profiles.Active.AuthPasswordHash;
        var renewed = auth.ChangePassword("WRONG", "newpass!", keep);
        Assert.That(renewed, Is.Null);
        Assert.That(profiles.Active.AuthPasswordHash, Is.EqualTo(originalHash),
            "Stored hash must not change on a failed authn");
        Assert.That(auth.Login("hunter2!", IPAddress.Loopback),
            Is.Not.Null, "Old password still works");
    }

    [Test]
    public void SetEnabled_TogglesProfileFlagOnAuthSuccess() {
        var (auth, profiles) = Make();
        auth.SetInitialPassword("hunter2!");
        Assert.That(profiles.Active.AuthEnabled, Is.True);
        Assert.That(auth.SetEnabled("hunter2!", false), Is.True);
        Assert.That(profiles.Active.AuthEnabled, Is.False);
        Assert.That(auth.SetEnabled("WRONG", true), Is.False,
            "Toggling requires the current password");
        Assert.That(profiles.Active.AuthEnabled, Is.False);
        Assert.That(auth.SetEnabled("hunter2!", true), Is.True);
        Assert.That(profiles.Active.AuthEnabled, Is.True);
    }

    [Test]
    public void GetStatus_ReportsConfiguredEnabledAuthenticatedFields() {
        var (auth, _) = Make();
        var s0 = auth.GetStatus(null);
        Assert.That(s0.Configured, Is.False);
        Assert.That(s0.Enabled, Is.True);
        Assert.That(s0.Authenticated, Is.False);
        auth.SetInitialPassword("hunter2!");
        var token = auth.Login("hunter2!", IPAddress.Loopback);
        var s1 = auth.GetStatus(token);
        Assert.That(s1.Configured, Is.True);
        Assert.That(s1.Enabled, Is.True);
        Assert.That(s1.Authenticated, Is.True);
        var s2 = auth.GetStatus("garbage");
        Assert.That(s2.Authenticated, Is.False);
    }

    [Test]
    public void ValidateToken_NullOrEmpty_ReturnsFalse() {
        var (auth, _) = Make();
        Assert.That(auth.ValidateToken(null), Is.False);
        Assert.That(auth.ValidateToken(""), Is.False);
    }
}
