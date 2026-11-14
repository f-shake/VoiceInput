using System.Reflection;

namespace VoiceInput.App.Ui;

/// <summary>
/// 程序图标。来源是用户提供的 SVG，构建前用 Edge 无头模式栅格化成 9 个尺寸的
/// .ico（16 到 256），作为嵌入资源编进程序：托盘要多大就有多大的原生位图，
/// 不再靠放大，也不依赖系统字体里有没有那个字形。
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "voiceinput.ico";

    private static readonly Lazy<Icon> LazyShared = new(() => Load(32));

    /// <summary>全进程共用一份，别 dispose：托盘和各个窗口都指着它。</summary>
    public static Icon Shared => LazyShared.Value;

    public static Icon Load(int size)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return SystemIcons.Application;
            }

            return new Icon(stream, new Size(size, size));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException)
        {
            return SystemIcons.Application;
        }
    }
}
