using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FocusClip.Interop;
using FocusClip.Models;

namespace FocusClip.Services;

/// <summary>
/// 앱 아이콘을 추출/캐시한다. (FM의 아이콘 시스템 대응 — 단, WPF Image 는 알파를
/// 네이티브로 렌더하므로 AHK처럼 배경 평탄화는 불필요.) 경로가 낡으면 실행 중
/// 프로세스에서 복구(self-heal)하고, 디스크 PNG 캐시로 경로 분실에도 표시를 유지한다.
/// </summary>
public sealed class IconService
{
    private readonly Dictionary<string, ImageSource> _cache = new();
    private static string CacheDir { get; } = Path.Combine(ConfigService.Dir, "icons");

    public ImageSource? GetIcon(AppEntry app, int size = 32)
    {
        if (_cache.TryGetValue(app.Name, out var hit)) return hit;

        ImageSource? img = null;
        string? src = ResolvePath(app);
        if (src != null) img = ExtractFromFile(src, size);

        string png = Path.Combine(CacheDir, SafeName(app.Name) + ".png");
        if (img != null)
        {
            try { SavePng((BitmapSource)img, png); } catch { }
        }
        else if (File.Exists(png))
        {
            try { img = LoadPng(png); } catch { }
        }
        img ??= SystemFallback(); // 미설치/추출 실패 시 윈도우 기본 앱 아이콘(빈칸 방지)

        if (img != null) _cache[app.Name] = img;
        return img;
    }

    /// <summary>경로가 유효하면 그대로, 낡았으면 실행 중 프로세스에서 복구하고 app.ExePath 갱신.</summary>
    public static string? ResolvePath(AppEntry app)
    {
        if (!string.IsNullOrEmpty(app.ExePath) && File.Exists(app.ExePath)) return app.ExePath;

        string nameNoExe = app.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? app.ProcessName[..^4] : app.ProcessName;
        if (string.IsNullOrEmpty(nameNoExe)) return null;

        try
        {
            foreach (var p in Process.GetProcessesByName(nameNoExe))
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) { app.ExePath = path; return path; }
                }
                catch { /* 권한/비트수 차이로 접근 불가 → 다음 프로세스 */ }
            }
        }
        catch { }
        return null;
    }

    private static ImageSource? ExtractFromFile(string file, int size)
    {
        if (!File.Exists(file)) return null;
        var handles = new IntPtr[1];
        uint n = NativeMethods.PrivateExtractIcons(file, 0, size, size, handles, null, 1, 0);
        if (n < 1 || handles[0] == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { NativeMethods.DestroyIcon(handles[0]); }
    }

    /// <summary>경로에서 직접 아이콘을 추출(설정 UI의 실행 중 프로세스 목록용). 실패 시 기본 아이콘.</summary>
    public ImageSource? IconForPath(string? path, int size = 32)
        => (string.IsNullOrEmpty(path) ? null : ExtractFromFile(path, size)) ?? SystemFallback();

    /// <summary>윈도우 기본 앱 아이콘(공유 핸들 → DestroyIcon 하지 않음).</summary>
    private static ImageSource? SystemFallback()
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                System.Drawing.SystemIcons.Application.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static ImageSource LoadPng(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static string SafeName(string name)
        => string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}
