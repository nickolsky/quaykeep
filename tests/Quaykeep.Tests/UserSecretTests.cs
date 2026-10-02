using Quaykeep.Core.Storage;

namespace Quaykeep.Tests;

public class UserSecretTests
{
    [Fact]
    public void Secrets_Are_Stored_Encrypted_For_This_User()
    {
        var stored = UserSecret.Protect("token-123");
        Assert.True(UserSecret.IsProtected(stored));
        Assert.DoesNotContain("token-123", stored);
        Assert.Equal("token-123", UserSecret.Unprotect(stored));
        Assert.Equal("plain-old", UserSecret.Unprotect("plain-old")); // written before encryption: read as it is
        Assert.False(UserSecret.IsProtected("plain-old"));
        Assert.Null(UserSecret.Unprotect("dpapi:AAAA")); // another user's or PC's value
        Assert.Null(UserSecret.Unprotect(null));
    }
}
