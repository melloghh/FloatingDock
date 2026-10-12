using FloatingDock.Core.Services;
namespace FloatingDock.UI.Views;
internal static class L
{
    internal static string Text(string text)=>InterfaceText.Get(text,DockApp.Current.Settings.Current.Language);
    internal static string Format(string text,params object[] args)=>string.Format(Text(text),args);
}
