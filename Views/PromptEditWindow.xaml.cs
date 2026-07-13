using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FocusClip.Views;

/// <summary>프롬프트 추가/편집 창(그룹 + 제목 + 본문). 추가는 빈 값으로, 편집은 기존 값으로 연다.
/// 그룹 칸에 기존 이름을 고르면 그 팝업으로, 새 이름을 입력하면 그 이름의 팝업이 새로 생긴다.</summary>
public partial class PromptEditWindow : Window
{
    public string ResultGroup { get; private set; } = "";
    public string ResultTitle { get; private set; } = "";
    public string ResultText { get; private set; } = "";

    private readonly string[] _groupNames;

    public PromptEditWindow(string initialTitle = "", string initialText = "",
                            IEnumerable<string>? groupNames = null, string initialGroup = "")
    {
        InitializeComponent();
        _groupNames = groupNames?.ToArray() ?? Array.Empty<string>();
        GroupNameBox.Text = initialGroup ?? "";
        TitleBox.Text = initialTitle ?? "";
        BodyBox.Text = initialText ?? "";
        Loaded += (_, _) =>
        {
            // 그룹이 비어 있으면(첫 승격 등) 그룹부터, 다음 제목, 아니면 본문에 포커스
            if (string.IsNullOrEmpty(GroupNameBox.Text)) GroupNameBox.Focus();
            else if (string.IsNullOrEmpty(TitleBox.Text)) TitleBox.Focus();
            else { BodyBox.Focus(); BodyBox.CaretIndex = BodyBox.Text.Length; }
        };
    }

    /// <summary>▾: 기존 그룹 이름 목록을 메뉴로 보여 주고, 선택 시 그룹 칸에 채운다.</summary>
    private void GroupPick_Click(object sender, RoutedEventArgs e)
    {
        if (_groupNames.Length == 0) return;
        var menu = new ContextMenu();
        foreach (var n in _groupNames)
        {
            var mi = new MenuItem { Header = n.Replace("_", "__") }; // _ 는 액세스키 이스케이프
            string name = n;
            mi.Click += (_, _) => { GroupNameBox.Text = name; GroupNameBox.CaretIndex = name.Length; };
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
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
        ResultGroup = GroupNameBox.Text.Trim(); // 비어 있으면 서비스가 기본 그룹으로 처리
        ResultTitle = TitleBox.Text.Trim();
        ResultText = BodyBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
