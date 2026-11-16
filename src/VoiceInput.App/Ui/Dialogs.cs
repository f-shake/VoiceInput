namespace VoiceInput.App.Ui;

/// <summary>
/// 全程序统一的提示框。用 <see cref="TaskDialog"/>（.NET 9 起自带）而不是 MessageBox：
/// 后者是 Win95 时代的固定外观，在高分屏与深色主题下和系统其它窗口不是一套。
/// </summary>
public static class Dialogs
{
    public static void Show(
        IWin32Window? owner,
        string heading,
        string text,
        TaskDialogIcon? icon = null,
        string? detail = null)
    {
        var page = new TaskDialogPage
        {
            Caption = "VoiceInput",
            Heading = heading,
            Text = text,
            Icon = icon ?? TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK },
            DefaultButton = TaskDialogButton.OK,
        };

        if (!string.IsNullOrEmpty(detail))
        {
            page.Expander = new TaskDialogExpander(detail) { Expanded = false };
        }

        if (owner is null)
        {
            TaskDialog.ShowDialog(page);
        }
        else
        {
            TaskDialog.ShowDialog(owner, page);
        }
    }
}
