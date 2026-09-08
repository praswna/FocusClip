using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace FocusClip.Views;

/// <summary>프롬프트 추가/편집 창(제목 + 본문). 추가는 빈 값으로, 편집은 기존 값으로 연다.</summary>
public partial class PromptEditWindow : Window
{
    public string ResultTitle { get; private set; } = "";
    public string ResultText { get; private set; } = "";
    private readonly string _initialTitle;
    private readonly string _initialText;
    private bool _closeAccepted;

    public PromptEditWindow(string initialTitle = "", string initialText = "")
    {
        InitializeComponent();
        _initialTitle = initialTitle ?? "";
        _initialText = initialText ?? "";
        TitleBox.Text = _initialTitle;
        BodyBox.Text = _initialText;
        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            // 제목이 비어 있으면 제목부터, 아니면 본문에 포커스
            if (string.IsNullOrEmpty(TitleBox.Text)) TitleBox.Focus();
            else { BodyBox.Focus(); BodyBox.CaretIndex = BodyBox.Text.Length; }
        };
    }

    private void Body_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double size = BodyBox.FontSize + (e.Delta > 0 ? 1 : -1);
        BodyBox.FontSize = Math.Max(8, Math.Min(48, size));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 제목·본문이 모두 비면 저장하지 않고 창 유지(닫아버리면 취소와 구분이 안 돼 저장된 줄 오인)
        if (string.IsNullOrWhiteSpace(TitleBox.Text) && string.IsNullOrWhiteSpace(BodyBox.Text))
        {
            TitleBox.Focus();
            return;
        }
        ResultTitle = TitleBox.Text.Trim();
        ResultText = BodyBox.Text;
        _closeAccepted = true;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            Save_Click(sender, e);
        }
        else if (e.Key == Key.Escape) { e.Handled = true; TryCancel(); }
    }

    private bool ConfirmDiscard()
        => (TitleBox.Text == _initialTitle && BodyBox.Text == _initialText)
            || MessageBox.Show(this, "수정한 내용을 버릴까요?", "프롬프트 편집",
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
