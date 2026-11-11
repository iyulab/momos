using Momos.Host.Security;

namespace Momos.Host.Tests.Security;

public sealed class SecretMaskerTests
{
    [Theory]
    [InlineData("Host=db;Database=app;Username=u;Password=hunter2;Port=5432", "Host=db;Database=app;Username=u;Password=<REDACTED>;Port=5432")]
    [InlineData("pwd = 'p@ss w0rd' and more", "pwd = <REDACTED> and more")]
    [InlineData("\"api_key\": \"abc123def456\"", "\"api_key\": <REDACTED>")]
    [InlineData("postgres://app:s3cret@db.internal/app", "postgres://app:<REDACTED>@db.internal/app")]
    [InlineData("token ghp_0123456789abcdef0123456789abcdef0123 here", "token <REDACTED> here")]
    [InlineData("key AKIAABCDEFGHIJKLMNOP end", "key <REDACTED> end")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "-----BEGIN PRIVATE KEY (<REDACTED>)-----")]
    public void LooksLikeASecret_IsMasked(string input, string expected) =>
        Assert.Equal(expected, SecretMasker.Mask(input));

    [Theory]
    [InlineData("The Password property of the User class is hashed with bcrypt.")]
    [InlineData("src/Auth/PasswordHasher.cs")]
    [InlineData("secretary of state; passwords are stored hashed")]
    [InlineData("https://github.com/iyulab/momos/pull/12")]
    [InlineData("")]
    public void OrdinaryText_IsLeftAlone(string input) =>
        Assert.Equal(input, SecretMasker.Mask(input));
}
