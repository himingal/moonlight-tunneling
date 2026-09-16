using System.Windows.Media;
using MoonlightTunneling.Core;

namespace MoonlightTunneling.App.ViewModels;

public static class Palette
{
    public static readonly Brush Green = Make("#2BA86E");
    public static readonly Brush Yellow = Make("#D69A1E");
    public static readonly Brush Red = Make("#D23C3C");
    public static readonly Brush Gray = Make("#A3A1AA");
    public static readonly Brush Ink = Make("#3A3A40");

    private static Brush Make(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public static Brush ForLog(LogLevel l) => l switch
    {
        LogLevel.Success => Green,
        LogLevel.Warn => Yellow,
        LogLevel.Error => Red,
        _ => Ink,
    };
}

public sealed class LogLine(LogEntry e)
{
    public string Time { get; } = e.Time.ToString("HH:mm:ss");
    public string Message { get; } = e.Message;
    public Brush Brush { get; } = Palette.ForLog(e.Level);
    public override string ToString() => $"{Time}  {Message}";
}
