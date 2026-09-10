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
        set
        {
            _text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Snippet));
            OnPropertyChanged(nameof(SizeLabel)); // 글자 수 표시(편집으로 본문 길이가 바뀌는 경우)
            OnPropertyChanged(nameof(PinTitle));   // 고정 팝업 압축 카드도 함께 갱신(편집으로 본문이 바뀌는 경우)
            OnPropertyChanged(nameof(PinSubtitle));
        }
    }

    public string? FilePath { get; set; }               // 저장된 이미지 PNG 경로(복사 시 재로드)

    /// <summary>Windows 캡처 등 다른 프로그램이 만든 원본 파일을 참조하는지 여부.
    /// true인 파일은 핀 해제·편집 때 FocusClip이 삭제하지 않는다.</summary>
    public bool ExternalFile { get; set; }

    private ImageSource? _thumb;
    public ImageSource? Thumb                            // 카드 표시용 썸네일
    {
        get => _thumb;
        set { _thumb = value; OnPropertyChanged(); }
    }

    /// <summary>이미지 편집 결과가 압축 큐에서 처리되기 전까지만 잠시 보관하는 원본.</summary>
    public BitmapSource? FullImage { get; set; }

    /// <summary>미고정 이미지의 PNG 압축 데이터. 원본 BitmapSource를 계속 붙잡지 않으면서도
    /// 디스크에 저장하지 않고 붙여넣기·편집·드래그할 수 있게 한다.</summary>
    public byte[]? ImageBytes { get; set; }

    /// <summary>원본이 단일 이미지 큐에서 압축되는 중인지 여부.</summary>
    public bool ImageProcessing { get; set; }

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

    public string TimeLabel => Time.ToString("yyyy MM-dd HH:mm:ss");
    public string Snippet => IsImage ? "" : (Text.Length > 300 ? Text[..300] : Text);

    private string _sizeLabel = "";
    /// <summary>카드 하단의 크기 표시 — 텍스트·경로는 글자 수, 이미지는 본문 용량.
    /// 미리보기가 300자에서 잘려 한 줄짜리인지 문서 통째인지 구분이 안 되므로 곁들인다.
    /// 이미지는 파일을 두드려야 알 수 있어 <see cref="ReadBodyState"/> 결과로 채운다.</summary>
    public string SizeLabel
    {
        // 텍스트는 세는 게 공짜라 검사 전에도 바로 보여준다(방금 복사한 카드가 빈칸으로 남지 않게).
        get => _sizeLabel.Length == 0 && !IsImage ? $"{Text.Length:N0}자" : _sizeLabel;
        set { _sizeLabel = value; OnPropertyChanged(); }
    }

    private bool _bodyMissing;
    /// <summary>저장해 둔 본문 파일이 사라졌는지(본문 삭제는 완전 수동이라 생길 수 있다).
    /// 메모리에 본문이 남아 있으면 파일이 없어도 쓸 수 있으므로 false.</summary>
    public bool BodyMissing
    {
        get => _bodyMissing;
        set { _bodyMissing = value; OnPropertyChanged(); }
    }

    /// <summary>본문 파일을 살펴 (크기 표시, 유실 여부)를 계산한다.
    /// 디스크를 두드리므로 백그라운드에서 부르고, 결과 대입은 UI 스레드에서 한다.</summary>
    public (string Size, bool Missing) ReadBodyState()
    {
        // 텍스트·경로는 본문이 메모리에 있으므로 글자 수만 세면 된다.
        if (!IsImage) return ($"{Text.Length:N0}자", false);

        long bytes = ImageBytes?.Length ?? 0; // 미고정 이미지는 PNG 가 메모리에 있어 디스크를 안 봐도 된다
        bool missing = false;
        if (bytes == 0 && !string.IsNullOrEmpty(FilePath))
        {
            try
            {
                var info = new System.IO.FileInfo(FilePath);
                if (info.Exists) bytes = info.Length;
                else missing = true;
            }
            catch { }
        }
        return (bytes > 0 ? FormatBytes(bytes) : "", missing);
    }

    private static string FormatBytes(long n)
        => n >= 1024 * 1024 ? $"{n / 1024d / 1024d:0.#}MB" : $"{Math.Max(1, n / 1024)}KB";

    // ── 고정(핀) 팝업 압축 카드 표시 ──
    /// <summary>압축 카드의 주 텍스트 — 경로=파일·폴더명, 이미지=라벨, 텍스트=한 줄로 접은 요약.</summary>
    public string PinTitle => IsPath ? PathName : IsImage ? "이미지" : OneLine(Text);

    /// <summary>압축 카드의 보조(흐린) 텍스트 — 경로=축약 디렉터리, 그 외=복사 시각.</summary>
    public string PinSubtitle => IsPath ? PathDir : Time.ToString("MM-dd HH:mm");

    /// <summary>줄바꿈·연속 공백을 공백 하나로 접어 한 줄로 만든다 — 압축 카드 높이를 항목마다 일정하게 유지.
    /// (TextBlock 은 TextWrapping=NoWrap 이어도 개행 문자에서 줄을 바꾸므로 텍스트 자체를 접어야 한다.)</summary>
    private static string OneLine(string text)
    {
        string t = text.Length > 200 ? text[..200] : text; // 카드에 보이는 길이만 처리(긴 본문 전체를 훑지 않음)
        var sb = new System.Text.StringBuilder(t.Length);
        bool gap = false;
        foreach (char ch in t)
        {
            if (char.IsWhiteSpace(ch)) { gap = true; continue; }
            if (gap && sb.Length > 0) sb.Append(' ');
            gap = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

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
