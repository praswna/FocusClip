using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace FocusClip.Views;

/// <summary>클립 텍스트 편집기(C4). 휠로 글자 크기 조절, 덮어쓰기/새 클립 저장. (CM TextEditDialog/ZoomTextEdit 대응)</summary>
public partial class TextEditWindow : Window
{
    public enum Mode { Overwrite, New }

    public string ResultText { get; private set; } = "";
    public Mode SaveMode { get; private set; } = Mode.Overwrite;
    private readonly string _initialText;
    private bool _closeAccepted;

    public TextEditWindow(string initialText)
    {
        InitializeComponent();
        _initialText = initialText ?? "";
        Editor.Text = _initialText;
        Loaded += (_, _) => { Editor.Focus(); Editor.CaretIndex = Editor.Text.Length; };
        PreviewKeyDown += Window_PreviewKeyDown;
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
        _closeAccepted = true;
        DialogResult = true;
    }

    private void SaveNew_Click(object sender, RoutedEventArgs e)
    {
        ResultText = Editor.Text;
        SaveMode = Mode.New;
        _closeAccepted = true;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SaveNew_Click(sender, e);
            else Overwrite_Click(sender, e);
        }
        else if (e.Key == Key.Escape) { e.Handled = true; TryCancel(); }
    }

    private bool ConfirmDiscard()
        => Editor.Text == _initialText || MessageBox.Show(this, "수정한 내용을 버릴까요?", "텍스트 편집",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void TryCancel()
    {
        if (!ConfirmDiscard()) return;
        _closeAccepted = true;
        DialogResult = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => TryCancel();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeAccepted && !ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }
}
