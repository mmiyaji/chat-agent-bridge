using ChatAgentBridge.Core.Deployment;

namespace ChatAgentBridge.Core.Tests;

public sealed class AppInstanceKeyTests
{
    [Fact]
    public void SameFolderSpelledDifferentlyGetsOneKey()
    {
        var key = AppInstanceKey.For(@"C:\Users\Alice\AppData\Local\ChatAgentBridge");

        Assert.Equal(key, AppInstanceKey.For(@"c:\users\alice\appdata\local\chatagentbridge\"));
        Assert.Equal(key, AppInstanceKey.For(@"C:\Users\Alice\AppData\Local\Temp\..\ChatAgentBridge"));
    }

    [Fact]
    public void SeparateFoldersRunSideBySide()
        => Assert.NotEqual(AppInstanceKey.For(@"C:\Profiles\Work"), AppInstanceKey.For(@"C:\Profiles\Home"));

    [Fact]
    public void KeyHidesThePathAndHasAFixedShape()
    {
        var key = AppInstanceKey.For(@"C:\Users\Alice\Secret Project");

        Assert.DoesNotContain("Alice", key, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^ChatAgentBridge\\.Data\\.[0-9a-f]{32}$", key);
    }

    [Fact]
    public void DriveRootIsAccepted()
        => Assert.Equal(AppInstanceKey.For(@"D:\"), AppInstanceKey.For(@"d:\"));
}
