using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
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
    public const int MaxItems = 30;
    public static string SaveDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ClipboardSaver");
    private static readonly string HistoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "FocusClip", "clips.json");

    public ObservableCollection<ClipItem> Items { get; } = new();

    /// <summary>복사된 파일 경로 항목(경로 전용 팝업에서 관리). 메인 Items와 분리.</summary>
    public ObservableCollection<ClipItem> Paths { get; } = new();

    /// <summary>새 클립이 추가될 때 발생(토스트 알림용).</summary>
    public event Action<ClipItem>? ItemAdded;

    private HwndSource? _src;
    private readonly DispatcherTimer _debounce;
    private DispatcherTimer? _saveTimer;
    private DateTime _internalCopyAt = DateTime.MinValue;

    // 직렬화용 레코드 (System.Text.Json이 생성자 파라미터로 매핑)
    private record ClipRecord(bool IsImage, string Text, string? FilePath, string Hash, bool Pinned, DateTime Time);
    private record ClipStore(List<ClipRecord> Items, List<ClipRecord> Paths);

    public ClipboardService()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ReadClipboard(); };
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
        Insert(Paths, new ClipItem { IsPath = true, Text = path, Hash = hash });
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
        Insert(Items, new ClipItem { IsImage = false, Text = text, Hash = hash });
    }

    private void AddImage(BitmapSource img)
    {
        if (!img.IsFrozen && img.CanFreeze) img.Freeze();
        string hash = HashImage(img);
        if (Dedup(Items, hash)) return;

        // C7: 원본은 메모리에 즉시 보관(붙여넣기 가능) + PNG 저장은 백그라운드로.
        var item = new ClipItem
        {
            IsImage = true,
            Hash = hash,
            Thumb = MakeThumb(img),
            FullImage = img,
        };
        Insert(Items, item);
        SaveImageAsync(item, img);
    }

    /// <summary>이미지를 백그라운드로 저장. 저장 완료 시 이미 삭제된 항목이면 고아 파일을 정리한다.</summary>
    private static void SaveImageAsync(ClipItem item, BitmapSource img)
    {
        Task.Run(() =>
        {
            try
            {
                string path = SaveImage(img);
                if (item.Removed) { try { File.Delete(path); } catch { } } // 저장 전에 삭제됨 → 고아 방지
                else item.FilePath = path;
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
        // 용량 초과 시 뒤에서부터 '비핀' 항목만 제거(핀 항목은 보호).
        while (col.Count > MaxItems)
        {
            int idx = -1;
            for (int i = col.Count - 1; i >= 0; i--)
                if (!col[i].Pinned) { idx = i; break; }
            if (idx < 0) break; // 전부 핀이면 제거하지 않음
            col.RemoveAt(idx);
        }
        try { ItemAdded?.Invoke(item); } catch { }
        ScheduleSave();
    }

    /// <summary>카드 핀 토글: 핀이면 최상단으로, 핀 해제면 핀 구간 바로 아래로 이동.</summary>
    public void TogglePin(ClipItem item)
    {
        if (Items.IndexOf(item) < 0) return;
        if (!item.Pinned)
        {
            item.Pinned = true;
            int cur = Items.IndexOf(item);
            if (cur != 0) Items.Move(cur, 0);          // 최상단으로
        }
        else
        {
            item.Pinned = false;
            int remainingPinned = Items.Count(c => c.Pinned); // 이 항목 제외(이미 false)
            int target = Math.Min(remainingPinned, Items.Count - 1);
            int cur = Items.IndexOf(item);
            if (cur != target) Items.Move(cur, target); // 남은 핀 구간 바로 아래로
        }
        ScheduleSave();
    }

    /// <summary>C4: 텍스트 클립 내용 교체(해시 갱신 + 클립보드 반영).</summary>
    public void ReplaceText(ClipItem item, string newText)
    {
        if (item.IsImage) return;
        item.Text = newText; // Snippet 알림 포함
        item.Hash = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(newText)));
        try { MarkInternalCopy(); Clipboard.SetText(newText); } catch { }
    }

    /// <summary>C5: 이미지 클립을 편집본(주석 합성)으로 교체. 옛 파일 삭제 후 백그라운드 재저장.</summary>
    public void ReplaceImage(ClipItem item, BitmapSource newImg)
    {
        if (!item.IsImage) return;
        if (!newImg.IsFrozen && newImg.CanFreeze) newImg.Freeze();
        try { if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath)) File.Delete(item.FilePath); }
        catch { }
        item.FullImage = newImg;
        item.Thumb = MakeThumb(newImg);
        item.Hash = HashImage(newImg);
        item.FilePath = null;
        Task.Run(() => { try { item.FilePath = SaveImage(newImg); } catch { } });
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
        Insert(Items, new ClipItem { IsImage = false, Text = text, Hash = hash });
        try { MarkInternalCopy(); Clipboard.SetText(text); } catch { }
    }

    /// <summary>C5(새 클립): 편집한 이미지를 새 카드로 추가(비동기 저장) + 클립보드 반영.</summary>
    public void AddEditedImage(BitmapSource img)
    {
        if (!img.IsFrozen && img.CanFreeze) img.Freeze();
        var item = new ClipItem
        {
            IsImage = true,
            Hash = HashImage(img),
            Thumb = MakeThumb(img),
            FullImage = img,
        };
        Insert(Items, item);
        SaveImageAsync(item, img);
        try { MarkInternalCopy(); Clipboard.SetImage(img); } catch { }
    }

    /// <summary>클립 항목을 목록에서 제거하고 저장 파일도 삭제한다.</summary>
    public void Remove(ClipItem item)
    {
        item.Removed = true; // 진행 중인 비동기 저장이 끝나면 파일을 정리하도록 표식
        if (!Items.Remove(item)) Paths.Remove(item); // 경로 항목은 Paths에 있음
        try { if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath)) File.Delete(item.FilePath); }
        catch { }
        ScheduleSave();
    }

    private static string HashImage(BitmapSource bmp)
    {
        try
        {
            int stride = (bmp.PixelWidth * ((bmp.Format.BitsPerPixel + 7) / 8));
            var bytes = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(bytes, stride, 0);
            return Convert.ToHexString(MD5.HashData(bytes));
        }
        catch
        {
            return $"img_{bmp.PixelWidth}x{bmp.PixelHeight}_{DateTime.Now.Ticks}";
        }
    }

    private static string SaveImage(BitmapSource bmp)
    {
        Directory.CreateDirectory(SaveDir);
        string path = Path.Combine(SaveDir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    private static ImageSource MakeThumb(BitmapSource bmp, int maxW = 240)
    {
        double scale = Math.Min(1.0, (double)maxW / Math.Max(1, bmp.PixelWidth));
        if (scale >= 1.0) return bmp;
        var tb = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
        tb.Freeze();
        return tb;
    }

    // ── 영구 저장 (P004/P012) ──

    /// <summary>시작 시 clips.json에서 이전 히스토리 복원. Start() 호출 전에 불러야 한다. 이미지 Thumb/FullImage는 UI 블로킹 방지를 위해 백그라운드에서 비동기 로드된다.</summary>
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
                    // 항목을 먼저 추가하고 Thumb/FullImage는 백그라운드 로드 — 큰 PNG를 UI 스레드에서
                    // 동기 디코딩하면 30개 기준 수 초간 UI 가 멈춘다.
                    var item = new ClipItem { IsImage = true, Hash = r.Hash, FilePath = r.FilePath,
                        Pinned = r.Pinned, Time = r.Time };
                    Items.Add(item);
                    var path = r.FilePath;
                    Task.Run(() =>
                    {
                        var img = LoadBitmapFile(path);
                        if (img == null) return;
                        var thumb = MakeThumb(img);
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            item.FullImage = img;
                            item.Thumb = thumb;
                        });
                    });
                }
                else
                {
                    if (string.IsNullOrEmpty(r.Text)) continue;
                    Items.Add(new ClipItem { Text = r.Text, Hash = r.Hash, Pinned = r.Pinned, Time = r.Time });
                }
            }
            foreach (var r in store.Paths ?? [])
            {
                if (string.IsNullOrEmpty(r.Text)) continue;
                Paths.Add(new ClipItem { IsPath = true, Text = r.Text, Hash = r.Hash, Pinned = r.Pinned, Time = r.Time });
            }
        }
        catch { }
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
            // 컬렉션 접근은 UI 스레드에서, 파일 쓰기는 백그라운드로 분리.
            // File.WriteAllText 가 Dropbox 등 외부 잠금으로 블로킹되어도 UI가 멈추지 않는다.
            var store = new ClipStore(
                Items.Select(x => new ClipRecord(x.IsImage, x.Text ?? "", x.FilePath, x.Hash, x.Pinned, x.Time)).ToList(),
                Paths.Select(x => new ClipRecord(false, x.Text ?? "", null, x.Hash, x.Pinned, x.Time)).ToList());
            string json = JsonSerializer.Serialize(store);
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                    File.WriteAllText(HistoryPath, json);
                }
                catch { }
            });
        }
        catch { }
    }

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

    /// <summary>클립보드 리스너 해제. 종료 시 Task.Run 완료를 보장할 수 없으므로 히스토리를 동기 저장 후 반환.</summary>
    public void Dispose()
    {
        _saveTimer?.Stop();
        // 종료 시에는 Task.Run 완료를 보장할 수 없으므로 동기 저장.
        try
        {
            var store = new ClipStore(
                Items.Select(x => new ClipRecord(x.IsImage, x.Text ?? "", x.FilePath, x.Hash, x.Pinned, x.Time)).ToList(),
                Paths.Select(x => new ClipRecord(false, x.Text ?? "", null, x.Hash, x.Pinned, x.Time)).ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(store));
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
