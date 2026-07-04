using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FocusClip.Models;

/// <summary>사용자가 직접 등록·관리하는 프롬프트 1개(제목 + 본문). 클립보드 히스토리와 달리 항상 파일로 유지된다.</summary>
public class PromptItem : INotifyPropertyChanged
{
    private string _title = "";
    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); }
    }

    private string _text = "";
    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); OnPropertyChanged(nameof(Preview)); }
    }

    /// <summary>카드 제목: Title이 비면 본문 첫 줄로 폴백.</summary>
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_title)) return _title.Trim();
            string first = FirstLine(_text);
            return string.IsNullOrWhiteSpace(first) ? "(빈 프롬프트)" : first;
        }
    }

    /// <summary>카드 보조 표시: 본문 앞부분(개행은 공백으로).</summary>
    public string Preview
    {
        get
        {
            string t = (_text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 120 ? t[..120] : t;
        }
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOfAny(new[] { '\r', '\n' });
        string line = (i < 0 ? s : s[..i]).Trim();
        return line.Length > 80 ? line[..80] : line;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
