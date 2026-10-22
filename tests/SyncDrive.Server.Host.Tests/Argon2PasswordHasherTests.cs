using SyncDrive.Infra.Security;

namespace SyncDrive.Server.Host.Tests;

public class Argon2PasswordHasherTests
{
    private static readonly Argon2Options Cheap = new(MemoryKiB: 64, Iterations: 1, Parallelism: 1);

    [Fact]
    public void Correct_password_verifies_and_wrong_one_does_not()
    {
        var hasher = new Argon2PasswordHasher(Cheap);
        var hash = hasher.Hash("correct-horse-battery");

        Assert.True(hasher.Verify("correct-horse-battery", hash));
        Assert.False(hasher.Verify("Correct-horse-battery", hash));
        Assert.False(hasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void Every_hash_has_its_own_salt()
    {
        var hasher = new Argon2PasswordHasher(Cheap);

        Assert.NotEqual(hasher.Hash("same-password-1"), hasher.Hash("same-password-1"));
    }

    [Fact]
    public void Hash_carries_its_parameters_so_old_hashes_survive_a_cost_increase()
    {
        var old = new Argon2PasswordHasher(Cheap).Hash("long-enough-password");

        var stronger = new Argon2PasswordHasher(new Argon2Options(MemoryKiB: 128, Iterations: 2, Parallelism: 1));

        Assert.StartsWith("argon2id$m=64,t=1,p=1$", old, StringComparison.Ordinal);
        Assert.True(stronger.Verify("long-enough-password", old));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("argon2id$m=64,t=1,p=1$!!!$???")]
    [InlineData("argon2id$m=0,t=0,p=0$AAAA$AAAA")]
    [InlineData("bcrypt$m=64,t=1,p=1$AAAA$AAAA")]
    public void Malformed_stored_hashes_never_verify_and_never_throw(string stored)
    {
        var hasher = new Argon2PasswordHasher(Cheap);

        Assert.False(hasher.Verify("whatever-password", stored));
    }
}
