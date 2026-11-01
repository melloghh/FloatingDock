using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FloatingDock.UI.Views;

/// <summary>Shared constants and the "animated content" reveal used when a page or panel appears.</summary>
internal static class Pop
{
    /// <summary>
    /// Transparent margin around every popping surface. The window is this much larger than the panel on each side,
    /// so the spring overshoot is drawn inside the window and never clipped by its edge.
    /// </summary>
    internal const double PadDip = 28;
    internal static readonly Thickness Pad = new(PadDip);

    /// <summary>
    /// Content rises by <paramref name="distance"/> DIPs, fades in and sharpens from a blurred copy of itself.
    /// <paramref name="host"/> must be a panel whose only job is to contain <paramref name="content"/>.
    /// </summary>
    internal static void Reveal(FrameworkElement host, FrameworkElement content, int milliseconds = 650, float distance = 22, float blur = 10)
    {
        try
        {
            if (!SurfaceMotion.Enabled || content.ActualWidth < 1 || content.ActualHeight < 1) return;
            ElementCompositionPreview.SetIsTranslationEnabled(host, true);
            var hostVisual = ElementCompositionPreview.GetElementVisual(host);
            var contentVisual = ElementCompositionPreview.GetElementVisual(content);
            var compositor = contentVisual.Compositor;
            var span = TimeSpan.FromMilliseconds(milliseconds);
            using var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(.22f, .7f), new Vector2(.2f, 1f));

            using var rise = compositor.CreateVector3KeyFrameAnimation();
            rise.Duration = span; rise.InsertKeyFrame(0, new Vector3(0, distance, 0)); rise.InsertKeyFrame(1, Vector3.Zero, ease);
            hostVisual.StartAnimation("Translation", rise);

            contentVisual.StopAnimation("Opacity");
            using var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(milliseconds * .7);
            fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, ease);
            contentVisual.StartAnimation("Opacity", fade);

            // A blurred copy of the content sits above it and dissolves while the blur radius falls to zero.
            var surface = compositor.CreateVisualSurface();
            surface.SourceVisual = contentVisual;
            surface.SourceSize = new Vector2((float)content.ActualWidth, (float)content.ActualHeight);
            var effect = new GaussianBlurEffect
            {
                Name = "Blur", BlurAmount = blur, BorderMode = EffectBorderMode.Hard,
                Source = new CompositionEffectSourceParameter("Source"),
            };
            using var factory = compositor.CreateEffectFactory(effect, new[] { "Blur.BlurAmount" });
            var brush = factory.CreateBrush();
            brush.SetSourceParameter("Source", compositor.CreateSurfaceBrush(surface));
            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = brush; sprite.Size = surface.SourceSize;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            using var sharpen = compositor.CreateScalarKeyFrameAnimation();
            sharpen.Duration = span; sharpen.InsertKeyFrame(0, blur); sharpen.InsertKeyFrame(1, 0, ease);
            brush.StartAnimation("Blur.BlurAmount", sharpen);
            using var dissolve = compositor.CreateScalarKeyFrameAnimation();
            dissolve.Duration = span; dissolve.InsertKeyFrame(0, 1); dissolve.InsertKeyFrame(.8f, 0, ease); dissolve.InsertKeyFrame(1, 0);
            sprite.StartAnimation("Opacity", dissolve);
            batch.End();
            batch.Completed += (_, _) =>
            {
                ElementCompositionPreview.SetElementChildVisual(host, null);
                sprite.Dispose(); brush.Dispose(); surface.Dispose(); batch.Dispose();
            };
        }
        catch (Exception ex)
        {
            // Effects are decoration: if the compositor refuses them the content is simply shown.
            DockApp.TryLog("content reveal", ex);
            ElementCompositionPreview.SetElementChildVisual(host, null);
            ElementCompositionPreview.GetElementVisual(content).Opacity = 1;
        }
    }
}
