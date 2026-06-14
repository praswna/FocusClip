using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusClip.Models;

/// <summary>클립보드 항목 1개(텍스트 또는 이미지). (CM의 ClipCard 데이터 대응)</summary>
public class ClipItem : INotifyPropertyChanged
{
    public bool IsImage { get; set; }

    /// <summary>파일 경로 항목 여부(경로 전용 팝업에서 관리). Text=전체 경로.</summary>
    public bool IsPath { get; set; }

    private string _text = "";
    public string Text                                  // 텍스트 내용(이미지면 빈 문자열)
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); OnPropertyChanged(nameof(Snippet)); }
    }

    public string? FilePath { get; set; }               // 저장된 이미지 PNG 경로(복사 시 재로드)

    private ImageSource? _thumb;
    public ImageSource? Thumb                            // 카드 표시용 썸네일
    {
        get => _thumb;
        set { _thumb = value; OnPropertyChanged(); }
    }

    /// <summary>원본 이미지(메모리). 비동기 저장 전/후 붙여넣기·편집에 사용.</summary>
    public BitmapSource? FullImage { get; set; }

    private bool _pinned;
    public bool Pinned                                   // 카드 핀(이력 보호 + 상단 고정)
    {
        get => _pinned;
        set { _pinned = value; OnPropertyChanged(); }
    }

    public DateTime Time { get; set; } = DateTime.Now;
    public string Hash { get; set; } = "";

    /// <summary>목록에서 제거됨 표식. 비동기 이미지 저장이 끝났을 때 고아 파일을 정리하기 위함.</summary>
    public volatile bool Removed;

    public string TimeLabel => Time.ToString("HH:mm:ss");
    public string Snippet => IsImage ? "" : (Text.Length > 300 ? Text[..300] : Text);

    // ── 경로 항목 함축 표시 ──
    /// <summary>경로의 마지막 세그먼트(파일/폴더명) — 카드 주 텍스트.</summary>
    public string PathName
    {
        get
        {
            string t = Text.Trim().TrimEnd('\\', '/');
            if (t.Length == 0) return Text;
            string name = System.IO.Path.GetFileName(t);
            return string.IsNullOrEmpty(name) ? t : name; // 루트(C:\) 등은 원문
        }
    }

    /// <summary>부모 경로를 중간 생략한 보조 텍스트(예: C:\…\상위폴더).</summary>
    public string PathDir
    {
        get
        {
            string t = Text.Trim().TrimEnd('\\', '/');
            string? dir = null;
            try { dir = System.IO.Path.GetDirectoryName(t); } catch { }
            if (string.IsNullOrEmpty(dir)) return "";
            var parts = dir.Split('\\', '/');
            if (parts.Length <= 2) return dir;                 // 짧으면 그대로
            return $"{parts[0]}\\…\\{parts[^1]}";              // 루트…\상위폴더
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
