using System;
using System.Windows;
using System.Windows.Input;

namespace FocusClip.Views;

/// <summary>클립 텍스트 편집기(C4). 휠로 글자 크기 조절, 덮어쓰기/새 클립 저장. (CM TextEditDialog/ZoomTextEdit 대응)</summary>
public partial class TextEditWindow : Window
{
    public enum Mode { Overwrite, New }

    public string ResultText { get; private set; } = "";
    public Mode SaveMode { get; private set; } = Mode.Overwrite;

    public TextEditWindow(string initialText)
    {
        InitializeComponent();
        Editor.Text = initialText ?? "";
        Loaded += (_, _) => { Editor.Focus(); Editor.CaretIndex = Editor.Text.Length; };
    }

    // CM ZoomTextEdit: 그냥 휠로 폰트 크기 ±
    private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double size = Editor.FontSize + (e.Delta > 0 ? 1 : -1);
        Editor.FontSize = Math.Max(8, Math.Min(48, size));
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        ResultText = Editor.Text;
        SaveMode = Mode.Overwrite;
        DialogResult = true;
    }

    private void SaveNew_Click(object sender, RoutedEventArgs e)
    {
        ResultText = Editor.Text;
        SaveMode = Mode.New;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
