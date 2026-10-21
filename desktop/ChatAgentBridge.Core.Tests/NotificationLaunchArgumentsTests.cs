using ChatAgentBridge_Desktop.Notifications;

namespace ChatAgentBridge.Core.Tests;

public sealed class NotificationLaunchArgumentsTests
{
    [Theory]
    [InlineData("----AppNotificationActivated:")]
    [InlineData("----AppNotificationActivated:payload")]
    public void RecognizesOnlySdkComLaunch(string argument) =>
        Assert.True(NotificationLaunchArguments.IsNotificationLaunch([argument]));

    [Fact]
    public void OrdinaryAndExplicitDataLaunchesKeepNormalStartup()
    {
        Assert.False(NotificationLaunchArguments.IsNotificationLaunch([]));
        Assert.False(NotificationLaunchArguments.IsNotificationLaunch(["--data-dir", @"D:\fixture"]));
        Assert.False(NotificationLaunchArguments.IsNotificationLaunch(["--data-dir", @"D:\fixture", "----AppNotificationActivated:"]));
        Assert.False(NotificationLaunchArguments.IsNotificationLaunch(["prefix----AppNotificationActivated:"]));
    }
}
