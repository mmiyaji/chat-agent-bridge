using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    // Like WinUI Gallery's page entrance: the new page rises slightly while fading in.
    // Pages are shown by visibility rather than Frame navigation, so the content area is animated directly.
    private void PlayPageEntrance()
    {
        // Respect "Animation effects" being turned off in Windows settings.
        if (!_uiSettings.AnimationsEnabled) return;
        ElementCompositionPreview.SetIsTranslationEnabled(MainContentGrid, true);
        var visual = ElementCompositionPreview.GetElementVisual(MainContentGrid);
        var compositor = visual.Compositor;
        var decelerate = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, decelerate);
        fade.Duration = TimeSpan.FromMilliseconds(200);
        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0f, new Vector3(0, 14, 0));
        rise.InsertKeyFrame(1f, Vector3.Zero, decelerate);
        rise.Duration = TimeSpan.FromMilliseconds(250);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation("Translation", rise);
    }
}
