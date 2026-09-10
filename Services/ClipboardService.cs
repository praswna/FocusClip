using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusClip.Interop;
using FocusClip.Models;

namespace FocusClip.Services;

/// <summary>
/// 클립보드 변경을 감시(WM_CLIPBOARDUPDATE)하여 텍스트/이미지를 수집한다.
/// 파일 드롭 목록·경로/URL 텍스트는 메인 Items에서 분리해 별도 Paths 컬렉션으로 모은다.
/// (CM의 _check_clipboard/ClipWorker 대응 — 폴링 대신 OS 리스너로 더 정확/가볍게)
/// </summary>
public sealed class ClipboardService : IDisposable
{
    public const int MaxItems = 20;
    // 모든 앱 데이터는 ConfigService.Dir(=%LOCALAPPDATA%\FocusClip) 한 곳에 모은다.
    // 클립 본문은 용량이 크고 자주 생성·삭제되므로 OneDrive·로밍 비대상인 Local 에 둬야
    // 디하이드레이트(클라우드 전용)로 드래그/열기가 느려지지 않는다.
    // 클립 본문(텍스트 .txt / 이미지 .png)은 Win+Shift+S 스크린샷이 모이는 Pictures\Screenshots 에
    // 직접 저장한다(모든 클립을 한 폴더에 모으자는 요청). FocusClip 파일은 clip_* 이름이라 OS 스크린샷과
    // 섞여도 구분되고, 핀 해제 삭제도 자기 파일만 지운다. 옛 위치(LocalAppData\FocusClip\media)의 파일은
    // clips.json 에 절대경로로 남아 그대로 로드된다(본문 이전 불필요).
    public static string SaveDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    public static string TextDir { get; } = SaveDir;   // 텍스트도 Screenshots 루트에 직접
    public static string ImageDir { get; } = SaveDir;  // 이미지도 Screenshots 루트에 직접
    private static readonly string HistoryPath = Path.Combine(ConfigService.Dir, "clips.json");

    public ObservableCollection<ClipItem> Items { get; } = new();

    /// <summary>복사된 파일 경로 항목(경로 전용 팝업에서 관리). 메인 Items와 분리.</summary>
    public ObservableCollection<ClipItem> Paths { get; } = new();

    // 저장 정책: 미고정 클립은 메모리에만 둔다(디스크 미저장). 사용자가 고정(📌)한 항목만
    // 본문(.txt/.png)과 clips.json으로 남겨 재시작 후에도 유지한다. 고정 해제 시 다시 메모리 전용으로 되돌린다.

    /// <summary>새 클립이 추가될 때 발생(토스트 알림용).</summary>
    public event Action<ClipItem>? ItemAdded;

    private HwndSource? _src;
    private readonly DispatcherTimer _debounce;
    private DispatcherTimer? _saveTimer;
    private DateTime _internalCopyAt = DateTime.MinValue;
    private readonly Channel<ImageWork> _imageQueue;
    private readonly Task _imageWorker;
    private int _pathRefreshRunning;
    private DateTime _lastPathRefresh = DateTime.MinValue;
    private int _bodyRefreshRunning;
    private DateTime _lastBodyRefresh = DateTime.MinValue;
    private readonly object _screenshotLock = new();
    private readonly List<ScreenshotCandidate> _recentScreenshots = new();
    private FileSystemWatcher? _screenshotWatcher;

    // 직렬화용 레코드 (System.Text.Json이 생성자 파라미터로 매핑)
    private record ClipRecord(bool IsImage, string Text, string? FilePath, string Hash, bool Pinned, DateTime Time,
        bool ExternalFile = false);
    private record ClipStore(List<ClipRecord> Items, List<ClipRecord> Paths);
    private record ImageWork(BitmapSource Image, ClipItem? ReplaceItem, DateTime QueuedUtc);
    private record ScreenshotCandidate(string Path, DateTime SeenUtc);

    public ClipboardService()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ReadClipboard(); };
        _imageQueue = Channel.CreateUnbounded<ImageWork>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        StartScreenshotWatcher();
        _imageWorker = Task.Run(ProcessImageQueueAsync);
    }

    private void StartScreenshotWatcher()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            _screenshotWatcher = new FileSystemWatcher(SaveDir, "*.png")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _screenshotWatcher.Created += (_, e) => TrackScreenshot(e.FullPath);
            _screenshotWatcher.Changed += (_, e) => TrackScreenshot(e.FullPath);
            _screenshotWatcher.Renamed += (_, e) => TrackScreenshot(e.FullPath);
        }
        catch
        {
            _screenshotWatcher?.Dispose();
            _screenshotWatcher = null;
        }
    }

    private void TrackScreenshot(string path)
    {
        if (Path.GetFileName(path).StartsWith("clip_", StringComparison.OrdinalIgnoreCase)) return;
        lock (_screenshotLock)
        {
            _recentScreenshots.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            _recentScreenshots.Insert(0, new ScreenshotCandidate(path, DateTime.UtcNow));
            if (_recentScreenshots.Count > 24)
                _recentScreenshots.RemoveRange(24, _recentScreenshots.Count - 24);
        }
    }

    /// <summary>메시지 전용 창을 만들어 클립보드 리스너를 등록한다(UI 스레드에서 호출).</summary>
    public void Start()
    {
        var pars = new HwndSourceParameters("FocusClipClipboardListener")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE
        };
        _src = new HwndSource(pars);
        _src.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(_src.Handle);
    }

    /// <summary>우리가 클립보드를 설정한 직후의 변경 이벤트를 무시하기 위한 표식.</summary>
    public void MarkInternalCopy() => _internalCopyAt = DateTime.Now;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            _debounce.Stop();
            _debounce.Start(); // 소스 앱이 쓰기를 끝낼 시간을 준 뒤 읽음
        }
        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        if ((DateTime.Now - _internalCopyAt).TotalSeconds < 1.0) return; // 내부 복사 무시
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null) { AddImage(img); return; }
            }
            // 탐색기에서 복사한 파일/폴더(드롭 목록) → 경로 항목으로.
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    foreach (string? f in files)
                        if (!string.IsNullOrWhiteSpace(f)) AddPath(f);
                    return;
                }
            }
            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(t)) return;
                if (IsPathLike(t) || IsUrlLike(t)) AddPath(t.Trim()); // 경로/URL은 경로 팝업으로
                else AddText(t);
            }
        }
        catch { /* 다른 앱이 클립보드를 잠금 → 다음 이벤트에서 재시도 */ }
    }

    /// <summary>단일 줄의 드라이브/UNC 경로 패턴인지(존재 여부는 따지지 않음).</summary>
    private static bool IsPathLike(string t)
    {
        t = t.Trim();
        if (t.Length < 3 || t.Contains('\n')) return false; // 단일 줄만
        return System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z]:\\") || t.StartsWith(@"\\");
    }

    /// <summary>단일 줄의 http(s) URL인지.</summary>
    private static bool IsUrlLike(string t)
    {
        t = t.Trim();
        if (t.Contains('\n')) return false;
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private void AddPath(string path)
    {
        // 정규화 키로 dedup(대소문자/슬래시 차이로 같은 경로가 중복되지 않게). 표시는 원본 유지.
        string key = NormalizePathKey(path);
        string hash = "path:" + Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        if (Dedup(Paths, hash)) return;
        var item = new ClipItem { IsPath = true, Text = path, Hash = hash };
        Insert(Paths, item);       // 미고정 → 메모리 전용(디스크 미저장). 고정 시에만 파일로 남김.
        RefreshPathExists(item);   // 존재 여부도 백그라운드에서 검사해 갱신
    }

    /// <summary>경로 존재 여부를 백그라운드에서 검사해 item.PathExists를 갱신(UI 스레드 디스크 I/O 회피).</summary>
    private static void RefreshPathExists(ClipItem item)
    {
        Task.Run(() =>
        {
            bool ok = item.CheckPathExists();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => item.PathExists = ok);
        });
    }

    /// <summary>모든 경로 항목의 존재 여부를 백그라운드로 재검사(팝업 표시 직전 호출 → 세션 중 삭제 반영).</summary>
    public void RefreshPathExistsAll()
    {
        if ((DateTime.UtcNow - _lastPathRefresh).TotalSeconds < 2) return;
        if (Interlocked.Exchange(ref _pathRefreshRunning, 1) != 0) return;
        _lastPathRefresh = DateTime.UtcNow;
        var items = Paths.ToList();
        Task.Run(() =>
        {
            try
            {
                var results = items.Select(item => (item, exists: item.CheckPathExists())).ToList();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    foreach (var (item, exists) in results)
                        if (Paths.Contains(item)) item.PathExists = exists;
                });
            }
            finally { Interlocked.Exchange(ref _pathRefreshRunning, 0); }
        });
    }

    /// <summary>모든 클립의 본문 크기·유실 여부를 백그라운드로 재검사(팝업 표시 직전 호출).
    /// 이미지는 파일을 stat 해야 하므로 경로 검사와 같은 방식으로 UI 스레드를 비켜 간다.</summary>
    public void RefreshBodyStateAll()
    {
        if ((DateTime.UtcNow - _lastBodyRefresh).TotalSeconds < 2) return;
        if (Interlocked.Exchange(ref _bodyRefreshRunning, 1) != 0) return;
        _lastBodyRefresh = DateTime.UtcNow;
        var items = Items.ToList();
        Task.Run(() =>
        {
            try
            {
                var results = items.Select(item => (item, state: item.ReadBodyState())).ToList();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    foreach (var (item, state) in results)
                        if (Items.Contains(item))
                        {
                            item.SizeLabel = state.Size;
                            item.BodyMissing = state.Missing;
                        }
                });
            }
            finally { Interlocked.Exchange(ref _bodyRefreshRunning, 0); }
        });
    }

    /// <summary>dedup용 경로 정규화. 로컬은 슬래시 통일+소문자, URL은 후행 슬래시만 정리.</summary>
    private static string NormalizePathKey(string p)
    {
        p = p.Trim();
        if (IsUrlLike(p)) return p.TrimEnd('/'); // URL은 대소문자가 경로를 구분할 수 있어 보존
        return p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    private void AddText(string text)
    {
        string hash = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        if (Dedup(Items, hash)) return;
        var item = new ClipItem { IsImage = false, Text = text, Hash = hash };
        Insert(Items, item);       // 미고정 → 메모리 전용(디스크 미저장). 고정 시에만 파일로 남김.
    }

    /// <summary>고정된 텍스트/경로 본문을 백그라운드로 .txt 저장하고 FilePath를 채운다(이미지의 SaveImageAsync와 동일 패턴 —
    /// UI 스레드에서 디스크 쓰기로 멈추지 않게). 저장이 끝나면 clips.json을 갱신해 FilePath를 반영한다.</summary>
    private void SaveTextAsync(ClipItem item, string text)
    {
        Task.Run(() =>
        {
            string? path = TrySaveTextFile(text);
            if (path == null) return;
            item.FilePath = path;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ScheduleSave());
        });
    }

    /// <summary>텍스트/경로 클립을 TextDir(media\text)에 .txt 파일로 저장. 실패 시 null(인메모리 Text로만 유지).</summary>
    private static string? TrySaveTextFile(string text)
    {
        try
        {
            Directory.CreateDirectory(TextDir);
            string name = $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}.txt";
            string path = Path.Combine(TextDir, name);
            File.WriteAllText(path, text);
            return path;
        }
        catch { return null; }
    }

    private void AddImage(BitmapSource img)
    {
        if (!img.IsFrozen && img.CanFreeze) img.Freeze();
        _imageQueue.Writer.TryWrite(new ImageWork(img, null, DateTime.UtcNow));
    }

    /// <summary>큰 이미지의 PNG 인코딩과 해시를 한 번에 하나씩 처리한다. 여러 캡처가 빠르게 들어와도
    /// CPU 집약 작업이 겹치지 않으며, 큐 순서대로 카드가 추가된다.</summary>
    private async Task ProcessImageQueueAsync()
    {
        await foreach (var work in _imageQueue.Reader.ReadAllAsync())
        {
            byte[]? bytes = null;
            string? screenshotPath = null;
            string? hash = null;
            try
            {
                if (work.ReplaceItem == null)
                    (screenshotPath, hash) = await TryMatchScreenshotAsync(work.Image, work.QueuedUtc);
                if (screenshotPath == null)
                {
                    bytes = EncodePng(work.Image);
                    hash = Convert.ToHexString(MD5.HashData(bytes));
                }
            }
            catch
            {
                if (work.ReplaceItem is { } failed)
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        failed.ImageProcessing = false;
                        if (failed.Pinned) SaveImageAsync(failed, work.Image);
                    });
                continue;
            }
            var thumb = screenshotPath != null
                ? LoadThumbFile(screenshotPath, 240)
                : LoadBitmapBytes(bytes!, 240);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (work.ReplaceItem is { } existing)
                {
                    if (!Items.Contains(existing)) return;
                    existing.Hash = hash!;
                    existing.Thumb = thumb;
                    existing.FullImage = null;
                    existing.ImageBytes = bytes;
                    existing.ImageProcessing = false;
                    existing.ExternalFile = false;
                    if (existing.Pinned && bytes != null) SaveImageBytesAsync(existing, bytes);
                    return;
                }
                if (Dedup(Items, hash!)) return;
                var item = new ClipItem
                {
                    IsImage = true,
                    Hash = hash!,
                    Thumb = thumb,
                    FilePath = screenshotPath,
                    ExternalFile = screenshotPath != null,
                    ImageBytes = bytes,
                };
                Insert(Items, item);
            });
        }
    }

    /// <summary>이미지를 백그라운드로 저장하고 FilePath를 채운다. 저장이 끝나면 풀해상도(FullImage)를 해제하고
    /// 썸네일을 파일 기반 독립 객체로 교체한다 — 캡처한 큰 이미지의 풀해상도가 세션 내내 RAM에 누적 상주하는 것을 막는다.
    /// (저장 전에는 FullImage가 유일한 원본이라 붙여넣기에 필요하고, 저장 후에는 FilePath에서 온디맨드 로드로 대체된다.)
    /// item.Removed 표식이 있으면 고아 파일을 정리하는 안전 경로가 있으나, 현재 삭제는 완전 수동이라 그 경로는 비활성.</summary>
    private void SaveImageAsync(ClipItem item, BitmapSource img)
    {
        Task.Run(() =>
        {
            try
            {
                string path = SaveImage(img);
                if (item.Removed) { try { File.Delete(path); } catch { } return; } // 안전 훅(현재 Removed는 설정되지 않음)
                // 저장 완료 → 풀해상도 해제. 썸네일은 파일에서 축소 디코딩한 독립 객체로 교체(원본 미참조).
                var thumb = LoadThumbFile(path, 240);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (item.Removed || !item.Pinned)
                    {
                        Task.Run(() => { try { File.Delete(path); } catch { } });
                        return;
                    }
                    item.FilePath = path;
                    if (thumb != null) item.Thumb = thumb;
                    item.FullImage = null;
                    ScheduleSave(); // 저장으로 FilePath가 채워졌으니 clips.json에 반영
                });
            }
            catch { }
        });
    }

    /// <summary>같은 해시가 있으면 핀 구간 바로 아래(최상단 비핀 위치)로 이동하고 true 반환.</summary>
    private static bool Dedup(ObservableCollection<ClipItem> col, string hash)
    {
        var existing = col.FirstOrDefault(c => c.Hash == hash);
        if (existing == null) return false;
        if (!existing.Pinned)
        {
            int i = col.IndexOf(existing);
            int top = PinnedCountFront(col);
            if (i > top) col.Move(i, top);
        }
        return true;
    }

    /// <summary>맨 앞에 연속으로 핀된 항목 수(핀 구간 경계).</summary>
    private static int PinnedCountFront(ObservableCollection<ClipItem> col)
    {
        int n = 0;
        while (n < col.Count && col[n].Pinned) n++;
        return n;
    }

    private void Insert(ObservableCollection<ClipItem> col, ClipItem item)
    {
        // 새 항목은 핀 구간 바로 아래(비핀 최상단)에 넣는다.
        col.Insert(PinnedCountFront(col), item);
        // 용량 초과 시 뒤에서부터 '비핀' 항목만 목록에서 제거(핀 항목은 보호).
        // 본문 파일은 지우지 않는다 — media 폴더는 사용자가 직접 관리(수동 삭제). 팝업은 최근 N개만 보여주는 뷰.
        while (col.Count > MaxItems)
        {
            int idx = -1;
            for (int i = col.Count - 1; i >= 0; i--)
                if (!col[i].Pinned) { idx = i; break; }
            if (idx < 0) break; // 전부 핀이면 제거하지 않음
            var removed = col[idx];
            col.RemoveAt(idx);
            removed.Removed = true;
            removed.FullImage = null;
            removed.ImageBytes = null;
        }
        try { ItemAdded?.Invoke(item); } catch { }
        ScheduleSave();
    }

    /// <summary>카드 핀 토글: 핀이면 최상단으로, 핀 해제면 핀 구간 바로 아래로 이동.
    /// 항목이 속한 컬렉션(Items 또는 Paths)을 자동 판별해 양쪽 팝업에서 동작한다.</summary>
    public void TogglePin(ClipItem item)
    {
        var col = Items.Contains(item) ? Items : Paths.Contains(item) ? Paths : null;
        if (col == null) return;
        if (!item.Pinned)
        {
            item.Pinned = true;
            int cur = col.IndexOf(item);
            if (cur != 0) col.Move(cur, 0);            // 최상단으로
            PersistPinned(item);                        // 고정 → 본문을 파일로 남김
        }
        else
        {
            item.Pinned = false;
            int remainingPinned = col.Count(c => c.Pinned); // 이 항목 제외(이미 false)
            int target = Math.Min(remainingPinned, col.Count - 1);
            int cur = col.IndexOf(item);
            if (cur != target) col.Move(cur, target);  // 남은 핀 구간 바로 아래로
            UnpersistUnpinned(item);                    // 고정 해제 → 파일 제거(메모리 전용으로 복귀)
        }
        ScheduleSave();
    }

    /// <summary>고정된 항목의 본문을 디스크에 저장(텍스트·경로=.txt, 이미지=.png). 이미 유효한 파일이 있으면
    /// 재저장하지 않는다. 미고정 항목은 메모리에만 있으므로, 고정하는 순간에만 파일로 남긴다.</summary>
    private void PersistPinned(ClipItem item)
    {
        if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath)) return;
        if (item.IsImage)
        {
            if (item.ImageProcessing) return; // 큐 완료 시 고정 상태를 보고 한 번만 저장
            if (item.ImageBytes is { Length: > 0 } bytes) SaveImageBytesAsync(item, bytes);
            else if (item.FullImage is { } img) SaveImageAsync(item, img);
        }
        else
        {
            SaveTextAsync(item, item.Text);
        }
    }

    /// <summary>고정 해제된 항목을 다시 메모리 전용으로 되돌린다 — 본문 파일을 삭제하고 FilePath를 비운다.
    /// 이미지가 파일에만 있던 경우(FullImage 해제됨) 삭제 전에 메모리로 다시 읽어 붙여넣기가 계속 되게 한다.</summary>
    private void UnpersistUnpinned(ClipItem item)
    {
        string? path = item.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        if (item.ExternalFile) return; // Windows가 만든 원본은 경로만 유지하고 삭제하지 않음
        if (item.IsImage && item.FullImage == null && item.ImageBytes == null)
        {
            Task.Run(() =>
            {
                byte[]? bytes = null;
                try { bytes = File.ReadAllBytes(path); } catch { }
                if (bytes == null) return;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (item.Pinned || item.FilePath != path) return;
                    item.ImageBytes = bytes;
                    item.FilePath = null;
                    Task.Run(() => { try { File.Delete(path); } catch { } });
                });
            });
            return;
        }
        item.FilePath = null;
        Task.Run(() => { try { File.Delete(path); } catch { } });
    }

    /// <summary>C4: 텍스트 클립 내용 교체(해시 갱신 + 클립보드 반영).</summary>
    public void ReplaceText(ClipItem item, string newText)
    {
        if (item.IsImage) return;
        try { if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath)) File.Delete(item.FilePath); }
        catch { }
        item.Text = newText; // Snippet 알림 포함
        item.Hash = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(newText)));
        item.FilePath = item.Pinned ? TrySaveTextFile(newText) : null; // 고정 항목만 파일로 유지
        if (item.Pinned) ScheduleSave();
        try { MarkInternalCopy(); Clipboard.SetText(newText); } catch { }
    }

    /// <summary>C5: 이미지 클립을 편집본(주석 합성)으로 교체. 옛 파일 삭제 후 백그라운드 재저장.</summary>
    public void ReplaceImage(ClipItem item, BitmapSource newImg)
    {
        if (!item.IsImage) return;
        if (!newImg.IsFrozen && newImg.CanFreeze) newImg.Freeze();
        try { if (!item.ExternalFile && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath)) File.Delete(item.FilePath); }
        catch { }
        item.FilePath = null;
        item.ExternalFile = false;
        item.FullImage = newImg; // 큐 처리 전 짧은 구간에도 붙여넣기 가능
        item.ImageBytes = null;
        item.ImageProcessing = true;
        _imageQueue.Writer.TryWrite(new ImageWork(newImg, item, DateTime.UtcNow));
        try
        {
            MarkInternalCopy();
            Clipboard.SetImage(newImg);
        }
        catch { }
    }

    /// <summary>C4(새 클립): 편집한 텍스트를 새 카드로 추가 + 클립보드 반영.</summary>
    public void AddEditedText(string text)
    {
        string hash = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        Insert(Items, new ClipItem { IsImage = false, Text = text, Hash = hash }); // 새 카드는 미고정 → 메모리 전용
        try { MarkInternalCopy(); Clipboard.SetText(text); } catch { }
    }

    /// <summary>C5(새 클립): 편집한 이미지를 새 카드로 추가(비동기 저장) + 클립보드 반영.</summary>
    public void AddEditedImage(BitmapSource img)
    {
        if (!img.IsFrozen && img.CanFreeze) img.Freeze();
        AddImage(img);
        try { MarkInternalCopy(); Clipboard.SetImage(img); } catch { }
    }

    /// <summary>클립 항목을 목록에서만 제거한다. 본문 파일은 지우지 않는다 — media 폴더는 사용자가 직접 관리(완전 수동 삭제).</summary>
    public void Remove(ClipItem item)
    {
        RemoveForUndo(item);
        FinalizeRemove(item);
    }

    /// <summary>실행 취소 가능하도록 항목을 목록에서만 빼고 원본 데이터는 잠시 유지한다.</summary>
    public int RemoveForUndo(ClipItem item)
    {
        var collection = item.IsPath ? Paths : Items;
        int index = collection.IndexOf(item);
        if (index >= 0) collection.RemoveAt(index);
        ScheduleSave();
        return index;
    }

    public void RestoreRemoved(ClipItem item, int index)
    {
        var collection = item.IsPath ? Paths : Items;
        if (collection.Contains(item)) return;
        item.Removed = false;
        collection.Insert(Math.Max(0, Math.Min(index, collection.Count)), item);
        ScheduleSave();
    }

    public static void FinalizeRemove(ClipItem item)
    {
        item.Removed = true;
        item.FullImage = null;
        item.ImageBytes = null;
        item.ImageProcessing = false;
    }

    /// <summary>클립의 원본 이미지를 필요할 때만 복원한다. 미고정 항목은 PNG 바이트에서,
    /// 고정 항목은 저장 파일에서 읽으므로 풀해상도 비트맵이 상주하지 않는다.</summary>
    public static BitmapSource? LoadImage(ClipItem item)
    {
        if (item.FullImage != null) return item.FullImage;
        if (item.ImageBytes is { Length: > 0 } bytes) return LoadBitmapBytes(bytes);
        return string.IsNullOrEmpty(item.FilePath) ? null : LoadBitmapFile(item.FilePath);
    }

    /// <summary>클립보드 이벤트 전후에 Windows가 저장한 PNG를 최대 350ms 동안 찾는다.
    /// 크기와 정규화된 픽셀 해시가 모두 같을 때만 원본 경로로 연결한다.</summary>
    private async Task<(string? Path, string? Hash)> TryMatchScreenshotAsync(BitmapSource source, DateTime queuedUtc)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(350);
        var checkedVersions = new HashSet<(string Path, long SeenTicks)>();
        string? sourceHash = null;
        while (true)
        {
            List<ScreenshotCandidate> candidates;
            lock (_screenshotLock)
                candidates = _recentScreenshots
                    .Where(x => x.SeenUtc >= queuedUtc.AddSeconds(-2))
                    .ToList();

            foreach (var candidate in candidates)
            {
                if (!checkedVersions.Add((candidate.Path.ToUpperInvariant(), candidate.SeenUtc.Ticks))) continue;
                var image = LoadBitmapFile(candidate.Path);
                if (image == null) continue; // Changed 이벤트가 오면 새 버전으로 다음 회차 재시도
                if (image.PixelWidth == source.PixelWidth && image.PixelHeight == source.PixelHeight
                    && HashImage(image) == (sourceHash ??= HashImage(source)))
                    return (candidate.Path, sourceHash);
            }

            if (DateTime.UtcNow >= deadline) return (null, null);
            await Task.Delay(80).ConfigureAwait(false);
        }
    }

    private static string HashImage(BitmapSource bmp)
    {
        try
        {
            BitmapSource normalized = bmp;
            if (bmp.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                normalized = converted;
            }
            int stride = normalized.PixelWidth * 4;
            int rowsPerChunk = Math.Max(1, 81920 / Math.Max(1, stride));
            var buffer = new byte[rowsPerChunk * stride];
            using var md5 = MD5.Create();
            for (int y = 0; y < normalized.PixelHeight; y += rowsPerChunk)
            {
                int rows = Math.Min(rowsPerChunk, normalized.PixelHeight - y);
                normalized.CopyPixels(new Int32Rect(0, y, normalized.PixelWidth, rows), buffer, stride, 0);
                md5.TransformBlock(buffer, 0, rows * stride, null, 0);
            }
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(md5.Hash!);
        }
        catch { return $"img_{bmp.PixelWidth}x{bmp.PixelHeight}_{DateTime.UtcNow.Ticks}"; }
    }

    private static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource? LoadBitmapBytes(byte[] bytes, int? decodeWidth = null)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth.HasValue) b.DecodePixelWidth = decodeWidth.Value;
            b.StreamSource = ms;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    private void SaveImageBytesAsync(ClipItem item, byte[] bytes)
    {
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(ImageDir);
                string path = Path.Combine(ImageDir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}.png");
                File.WriteAllBytes(path, bytes);
                if (item.Removed) { try { File.Delete(path); } catch { } return; }
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (item.Removed || !item.Pinned)
                    {
                        Task.Run(() => { try { File.Delete(path); } catch { } });
                        return;
                    }
                    item.FilePath = path;
                    item.ImageBytes = null;
                    item.FullImage = null;
                    ScheduleSave();
                });
            }
            catch { }
        });
    }

    private static string SaveImage(BitmapSource bmp)
    {
        Directory.CreateDirectory(ImageDir);
        string path = Path.Combine(ImageDir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    // ── 영구 저장 (P004/P012) ──

    /// <summary>시작 시 clips.json에서 고정 히스토리 복원(저장된 항목은 모두 고정 항목). Start() 호출 전에 불러야 한다.
    /// 이미지 썸네일은 UI 블로킹 방지를 위해 백그라운드에서 축소 디코딩으로 비동기 로드되며, 풀해상도는 상주시키지 않는다(붙여넣기/편집 시 FilePath에서 로드).</summary>
    public void LoadHistory()
    {
        if (!File.Exists(HistoryPath)) return;
        try
        {
            var json = File.ReadAllText(HistoryPath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var store = JsonSerializer.Deserialize<ClipStore>(json, opts);
            if (store == null) return;
            foreach (var r in store.Items ?? [])
            {
                if (r.IsImage)
                {
                    if (string.IsNullOrEmpty(r.FilePath) || !File.Exists(r.FilePath)) continue;
                    // 항목을 먼저 추가하고 썸네일은 백그라운드 로드 — 큰 PNG를 UI 스레드에서
                    // 동기 디코딩하면 보관 개수(최대 20개)만큼 누적돼 수 초간 UI 가 멈춘다.
                    // FullImage(풀해상도)는 보관하지 않는다 — 붙여넣기/편집은 FilePath에서 온디맨드 로드하므로
                    // 히스토리 이미지마다 풀해상도를 RAM에 상주시키는 메모리 낭비를 피한다.
                    var item = new ClipItem { IsImage = true, Hash = r.Hash, FilePath = r.FilePath,
                        ExternalFile = r.ExternalFile, Pinned = r.Pinned, Time = r.Time };
                    Items.Add(item);
                    var path = r.FilePath;
                    Task.Run(() =>
                    {
                        var thumb = LoadThumbFile(path, 240);
                        if (thumb == null) return;
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => item.Thumb = thumb);
                    });
                }
                else
                {
                    string text = ReadTextRecord(r);
                    if (string.IsNullOrEmpty(text)) continue;
                    Items.Add(new ClipItem { Text = text, Hash = r.Hash, Pinned = r.Pinned, Time = r.Time, FilePath = r.FilePath });
                }
            }
            foreach (var r in store.Paths ?? [])
            {
                string text = ReadTextRecord(r);
                if (string.IsNullOrEmpty(text)) continue;
                var item = new ClipItem { IsPath = true, Text = text, Hash = r.Hash, Pinned = r.Pinned, Time = r.Time, FilePath = r.FilePath };
                Paths.Add(item);
                RefreshPathExists(item);
            }
        }
        catch { }
    }

    /// <summary>.txt 파일이 있으면 clips.json에는 본문을 중복 저장하지 않음(""). 파일 저장이
    /// 실패해 FilePath가 없는 경우에만 손실 방지를 위해 Text를 그대로 직렬화.</summary>
    private static string TextForJson(ClipItem x)
        => string.IsNullOrEmpty(x.FilePath) ? (x.Text ?? "") : "";

    /// <summary>텍스트/경로 레코드의 본문을 읽는다. .txt 파일이 있으면 그쪽을 우선(파일이 정본),
    /// 없으면 옛 형식(clips.json에 직접 저장된 Text) 호환을 위해 r.Text로 폴백.</summary>
    private static string ReadTextRecord(ClipRecord r)
    {
        if (!string.IsNullOrEmpty(r.FilePath) && File.Exists(r.FilePath))
        {
            try { return File.ReadAllText(r.FilePath); } catch { }
        }
        return r.Text;
    }

    // Insert/Remove/TogglePin 후 300ms 디바운스로 저장(연속 변경 시 파일 쓰기 최소화).
    private void ScheduleSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); DoSave(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>현재 Items·Paths를 clips.json으로 직렬화. UI 스레드에서 컬렉션을 읽고 파일 쓰기는 백그라운드 Task로 위임.</summary>
    private void DoSave()
    {
        try
        {
            // 컬렉션 접근은 UI 스레드에서, 파일 쓰기(WriteHistoryAtomic: 임시파일+교체)는 백그라운드 Task로 분리.
            // 디스크 I/O 지연(잠금·플러시 등)이 있어도 UI가 멈추지 않는다.
            // 고정 항목만 저장한다(미고정은 메모리 전용).
            var store = new ClipStore(
                Items.Where(x => x.Pinned).Select(x => new ClipRecord(x.IsImage, TextForJson(x), x.FilePath, x.Hash, x.Pinned, x.Time, x.ExternalFile)).ToList(),
                Paths.Where(x => x.Pinned).Select(x => new ClipRecord(false, TextForJson(x), x.FilePath, x.Hash, x.Pinned, x.Time)).ToList());
            string json = JsonSerializer.Serialize(store);
            Task.Run(() =>
            {
                try { WriteHistoryAtomic(json); }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>clips.json을 원자적으로 저장. 임시 파일에 쓴 뒤 교체(MoveFileEx replace)하므로,
    /// 쓰는 도중 크래시/전원차단으로 본 파일이 잘려 다음 시작 시 히스토리 전체가 소실되는 일을 막는다.</summary>
    private static void WriteHistoryAtomic(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        string tmp = HistoryPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, HistoryPath, true); // 같은 볼륨 → 원자적 교체(부분 쓰기로 인한 손상 방지)
    }

    /// <summary>파일에서 풀해상도 비트맵 로드. 고정 해제 시(UnpersistUnpinned) 파일만 있던 이미지를
    /// 삭제하기 전에 메모리로 다시 읽어 붙여넣기가 계속 되게 하는 데 쓴다.</summary>
    private static BitmapSource? LoadBitmapFile(string path)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    /// <summary>파일에서 축소 디코딩(DecodePixelWidth)으로 썸네일만 읽는다. 풀해상도를 메모리에 올리지 않아
    /// 히스토리 이미지가 많아도 메모리를 적게 쓴다. 디코딩 결과는 원본 비트맵을 참조하지 않는 독립 객체.</summary>
    private static BitmapSource? LoadThumbFile(string path, int maxW)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.DecodePixelWidth = maxW; // 큰 PNG도 maxW 폭으로만 디코딩(원본보다 작으면 약간 확대되지만 표시에는 무해)
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    /// <summary>클립보드 리스너 해제. 종료 시 Task.Run 완료를 보장할 수 없으므로 히스토리를 동기 저장 후 반환.</summary>
    public void Dispose()
    {
        _imageQueue.Writer.TryComplete();
        _screenshotWatcher?.Dispose();
        _screenshotWatcher = null;
        _saveTimer?.Stop();
        // 종료 시에는 Task.Run 완료를 보장할 수 없으므로 동기 저장. 고정 항목만 남긴다.
        try
        {
            var store = new ClipStore(
                Items.Where(x => x.Pinned).Select(x => new ClipRecord(x.IsImage, TextForJson(x), x.FilePath, x.Hash, x.Pinned, x.Time, x.ExternalFile)).ToList(),
                Paths.Where(x => x.Pinned).Select(x => new ClipRecord(false, TextForJson(x), x.FilePath, x.Hash, x.Pinned, x.Time)).ToList());
            WriteHistoryAtomic(JsonSerializer.Serialize(store));
        }
        catch { }
        if (_src != null)
        {
            try { NativeMethods.RemoveClipboardFormatListener(_src.Handle); } catch { }
            _src.RemoveHook(WndProc);
            _src.Dispose();
            _src = null;
        }
    }
}
