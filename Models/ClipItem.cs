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

    /// <summary>원본 이미지(메모리). 파일 저장 전까지만 보관해 붙여넣기에 쓰고, 저장 완료 후에는 해제(null)되어
    /// 붙여넣기·편집은 FilePath에서 온디맨드 로드한다. 히스토리에서 복원된 이미지는 처음부터 null.</summary>
    public BitmapSource? FullImage { get; set; }

    private bool _pinned;
    public bool Pinned                                   // 카드 핀(이력 보호 + 상단 고정)
    {
        get => _pinned;
        set { _pinned = value; OnPropertyChanged(); }
    }

    public DateTime Time { get; set; } = DateTime.Now;
    public string Hash { get; set; } = "";

    /// <summary>비동기 이미지 저장 완료 시 고아 파일을 정리하기 위한 표식. 단, 현재는 본문 삭제가
    /// 완전 수동(파일 보존)이라 어디서도 true로 설정하지 않으며, 안전 훅으로만 남겨 둔다.</summary>
    public volatile bool Removed;

    public string TimeLabel => Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string Snippet => IsImage ? "" : (Text.Length > 300 ? Text[..300] : Text);

    // ── 경로 항목 함축 표시 ──
    /// <summary>http(s) URL 여부.</summary>
    public bool IsUrl =>
        Text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        Text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private bool _pathExists = true; // 검사 전엔 존재 가정(팝업 즉시 표시). 백그라운드 검사로 갱신.
    /// <summary>로컬 경로 존재 여부(카드 흐림 표시용·바인딩). 디스크 검사는 <see cref="CheckPathExists"/>로
    /// 백그라운드에서 수행해 이 값을 갱신한다 — 렌더링마다 UI 스레드에서 디스크를 두드리지 않게.</summary>
    public bool PathExists
    {
        get => _pathExists;
        set { _pathExists = value; OnPropertyChanged(); }
    }

    /// <summary>경로 존재 여부를 디스크에서 직접 확인(URL·UNC는 true 가정). UI 블로킹 방지를 위해 백그라운드 호출 권장.</summary>
    public bool CheckPathExists()
    {
        if (IsUrl) return true;
        string t = Text.Trim();
        // UNC(\\server\...)는 오프라인일 때 File/Directory.Exists가 수 초간 블로킹하므로 존재 가정.
        if (t.StartsWith(@"\\")) return true;
        try { return System.IO.File.Exists(t) || System.IO.Directory.Exists(t); }
        catch { return false; }
    }

    /// <summary>경로/URL의 주 텍스트(파일·폴더명 또는 URL 마지막 세그먼트).</summary>
    public string PathName
    {
        get
        {
            string t = Text.Trim();
            if (IsUrl)
            {
                try
                {
                    var u = new Uri(t);
                    string seg = u.Segments.Length > 0 ? u.Segments[^1].Trim('/') : "";
                    return string.IsNullOrEmpty(seg) ? u.Host : seg;
                }
                catch { return t; }
            }
            t = t.TrimEnd('\\', '/');
            if (t.Length == 0) return Text;
            string name = System.IO.Path.GetFileName(t);
            return string.IsNullOrEmpty(name) ? t : name; // 루트(C:\) 등은 원문
        }
    }

    /// <summary>보조(흐린) 텍스트 — URL은 호스트, 로컬은 부모 경로 축약(C:\…\상위폴더).</summary>
    public string PathDir
    {
        get
        {
            string t = Text.Trim();
            if (IsUrl)
            {
                try { return new Uri(t).Host; } catch { return ""; }
            }
            t = t.TrimEnd('\\', '/');
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
