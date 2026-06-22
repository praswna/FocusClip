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
    public const int MaxItems = 20;
    // 모든 앱 데이터는 ConfigService.Dir(=%LOCALAPPDATA%\FocusClip) 한 곳에 모은다.
    // 클립 본문은 용량이 크고 자주 생성·삭제되므로 OneDrive·로밍 비대상인 Local 에 둬야
    // 디하이드레이트(클라우드 전용)로 드래그/열기가 느려지지 않는다.
    // 저장 루트(media) 아래 텍스트/이미지를 분리 보관. SaveDir은 '열기' 대상(부모) 및 호환용으로 유지.
    public static string SaveDir { get; } = Path.Combine(ConfigService.Dir, "media");
    public static string TextDir { get; } = Path.Combine(SaveDir, "text");
    public static string ImageDir { get; } = Path.Combine(SaveDir, "image");
    private static readonly string HistoryPath = Path.Combine(ConfigService.Dir, "clips.json");

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
        var item = new ClipItem { IsPath = true, Text = path, Hash = hash };
        Insert(Paths, item);
        SaveTextAsync(item, path); // 디스크 쓰기는 백그라운드(UI 비블로킹). Text는 메모리에 있어 즉시 사용 가능
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
        foreach (var item in Paths) RefreshPathExists(item);
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
        Insert(Items, item);
        SaveTextAsync(item, text); // 디스크 쓰기는 백그라운드(UI 비블로킹). Text는 메모리에 있어 즉시 사용 가능
    }

    /// <summary>텍스트/경로 본문을 백그라운드로 .txt 저장하고 FilePath를 채운다(이미지의 SaveImageAsync와 동일 패턴 —
    /// 캡처마다 UI 스레드에서 디스크 쓰기로 멈추지 않게). 저장 전에는 clips.json에 Text가 인라인 보존된다.</summary>
    private static void SaveTextAsync(ClipItem item, string text)
    {
        Task.Run(() =>
        {
            string? path = TrySaveTextFile(text);
            if (path != null) item.FilePath = path;
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
        // HashImage는 전체 픽셀을 스캔하므로 큰 캡처(4K)에서 UI를 멈칫하게 한다. 비트맵이 frozen이라
        // 해시는 백그라운드에서 계산하고, dedup·Insert(컬렉션 변경)·저장만 UI 스레드로 마샬한다.
        // 클립 처리는 150ms 디바운스로 직렬화되어, 백그라운드 해시 완료 전에 다음 캡처가 끼어들지 않으므로 순서가 유지된다.
        Task.Run(() =>
        {
            string hash = HashImage(img);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
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
            });
        });
    }

    /// <summary>이미지를 백그라운드로 저장하고 FilePath를 채운다. 저장이 끝나면 풀해상도(FullImage)를 해제하고
    /// 썸네일을 파일 기반 독립 객체로 교체한다 — 캡처한 큰 이미지의 풀해상도가 세션 내내 RAM에 누적 상주하는 것을 막는다.
    /// (저장 전에는 FullImage가 유일한 원본이라 붙여넣기에 필요하고, 저장 후에는 FilePath에서 온디맨드 로드로 대체된다.)
    /// item.Removed 표식이 있으면 고아 파일을 정리하는 안전 경로가 있으나, 현재 삭제는 완전 수동이라 그 경로는 비활성.</summary>
    private static void SaveImageAsync(ClipItem item, BitmapSource img)
    {
        Task.Run(() =>
        {
            try
            {
                string path = SaveImage(img);
                if (item.Removed) { try { File.Delete(path); } catch { } return; } // 안전 훅(현재 Removed는 설정되지 않음)
                item.FilePath = path;
                // 저장 완료 → 풀해상도 해제. 썸네일은 파일에서 축소 디코딩한 독립 객체로 교체(원본 미참조).
                var thumb = LoadThumbFile(path, 240);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (item.Removed) return;
                    if (thumb != null) item.Thumb = thumb;
                    item.FullImage = null;
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
            col.RemoveAt(idx);
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
        }
        else
        {
            item.Pinned = false;
            int remainingPinned = col.Count(c => c.Pinned); // 이 항목 제외(이미 false)
            int target = Math.Min(remainingPinned, col.Count - 1);
            int cur = col.IndexOf(item);
            if (cur != target) col.Move(cur, target);  // 남은 핀 구간 바로 아래로
        }
        ScheduleSave();
    }

    /// <summary>C4: 텍스트 클립 내용 교체(해시 갱신 + 클립보드 반영).</summary>
    public void ReplaceText(ClipItem item, string newText)
    {
        if (item.IsImage) return;
        try { if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath)) File.Delete(item.FilePath); }
        catch { }
        item.Text = newText; // Snippet 알림 포함
        item.Hash = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(newText)));
        item.FilePath = TrySaveTextFile(newText);
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
        string? file = TrySaveTextFile(text);
        Insert(Items, new ClipItem { IsImage = false, Text = text, Hash = hash, FilePath = file });
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

    /// <summary>클립 항목을 목록에서만 제거한다. 본문 파일은 지우지 않는다 — media 폴더는 사용자가 직접 관리(완전 수동 삭제).</summary>
    public void Remove(ClipItem item)
    {
        if (!Items.Remove(item)) Paths.Remove(item); // 경로 항목은 Paths에 있음
        ScheduleSave();
    }

    private static string HashImage(BitmapSource bmp)
    {
        try
        {
            int stride = bmp.PixelWidth * ((bmp.Format.BitsPerPixel + 7) / 8);
            int height = bmp.PixelHeight;
            // 전체 픽셀을 한 번에 할당하면 큰 이미지에서 수십 MB가 LOH에 잡힌다(매 복사마다 GC 부담).
            // 행을 작은 청크(<85KB, LOH 회피)로 나눠 CopyPixels → 증분 해싱한다. 행이 연속 배치되므로 결과 해시는 동일.
            int rowsPerChunk = Math.Max(1, 81920 / Math.Max(1, stride));
            var buf = new byte[rowsPerChunk * stride];
            using var md5 = MD5.Create();
            for (int y = 0; y < height; y += rowsPerChunk)
            {
                int rows = Math.Min(rowsPerChunk, height - y);
                bmp.CopyPixels(new Int32Rect(0, y, bmp.PixelWidth, rows), buf, stride, 0);
                md5.TransformBlock(buf, 0, rows * stride, null, 0);
            }
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(md5.Hash!);
        }
        catch
        {
            return $"img_{bmp.PixelWidth}x{bmp.PixelHeight}_{DateTime.Now.Ticks}";
        }
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

    /// <summary>표시용 축소 썸네일. 반환되는 TransformedBitmap은 원본 bmp를 참조(메모리에 핀)하므로,
    /// 캡처 이미지의 경우 저장 완료 후 SaveImageAsync가 파일 기반 독립 썸네일로 교체해 풀해상도를 해제한다.</summary>
    private static ImageSource MakeThumb(BitmapSource bmp, int maxW = 240)
    {
        double scale = Math.Min(1.0, (double)maxW / Math.Max(1, bmp.PixelWidth));
        if (scale >= 1.0) return bmp;
        var tb = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
        tb.Freeze();
        return tb;
    }

    // ── 영구 저장 (P004/P012) ──

    /// <summary>시작 시 clips.json에서 이전 히스토리 복원. Start() 호출 전에 불러야 한다. 이미지 썸네일은 UI 블로킹 방지를 위해 백그라운드에서 축소 디코딩으로 비동기 로드되며, 풀해상도는 상주시키지 않는다(붙여넣기/편집 시 FilePath에서 로드).</summary>
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
                        Pinned = r.Pinned, Time = r.Time };
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
            var store = new ClipStore(
                Items.Select(x => new ClipRecord(x.IsImage, TextForJson(x), x.FilePath, x.Hash, x.Pinned, x.Time)).ToList(),
                Paths.Select(x => new ClipRecord(false, TextForJson(x), x.FilePath, x.Hash, x.Pinned, x.Time)).ToList());
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

    /// <summary>(현재 미사용) 파일에서 풀해상도 비트맵 로드. LoadHistory가 LoadThumbFile(축소 디코딩)로 전환된 뒤
    /// 호출처가 없으며, 풀해상도 온디맨드 로드가 다시 필요할 때를 위한 헬퍼로 남겨 둔다. (붙여넣기/편집은 App.LoadImage 사용.)</summary>
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
        _saveTimer?.Stop();
        // 종료 시에는 Task.Run 완료를 보장할 수 없으므로 동기 저장.
        try
        {
            var store = new ClipStore(
                Items.Select(x => new ClipRecord(x.IsImage, TextForJson(x), x.FilePath, x.Hash, x.Pinned, x.Time)).ToList(),
                Paths.Select(x => new ClipRecord(false, TextForJson(x), x.FilePath, x.Hash, x.Pinned, x.Time)).ToList());
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
