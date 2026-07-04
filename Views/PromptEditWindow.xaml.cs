using System;
using System.Windows;
using System.Windows.Input;

namespace FocusClip.Views;

/// <summary>프롬프트 추가/편집 창(제목 + 본문). 추가는 빈 값으로, 편집은 기존 값으로 연다.</summary>
public partial class PromptEditWindow : Window
{
    public string ResultTitle { get; private set; } = "";
    public string ResultText { get; private set; } = "";

    public PromptEditWindow(string initialTitle = "", string initialText = "")
    {
        InitializeComponent();
        TitleBox.Text = initialTitle ?? "";
        BodyBox.Text = initialText ?? "";
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
        // 제목·본문이 모두 비면 저장하지 않음(빈 프롬프트 방지)
        if (string.IsNullOrWhiteSpace(TitleBox.Text) && string.IsNullOrWhiteSpace(BodyBox.Text))
        {
            DialogResult = false;
            return;
        }
        ResultTitle = TitleBox.Text.Trim();
        ResultText = BodyBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
