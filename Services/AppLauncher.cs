using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FocusClip.Models;

namespace FocusClip.Services;

/// <summary>
/// 등록 앱을 실제로 "실행"하는 층. 일반 exe 는 경로로 바로 띄우지만,
/// 스토어(MSIX) 패키지 앱 — ChatGPT 데스크톱, WhatsApp 등 — 은 실행 파일이
/// C:\Program Files\WindowsApps 아래에 있고 이 폴더는 일반 권한으로 열람/실행이 막혀 있어
/// File.Exists 조차 false 가 된다. 그래서 패키지 앱은 AUMID(앱 사용자 모델 ID)로
/// shell:AppsFolder 를 통해 띄운다(윈도우 시작 메뉴가 앱을 띄우는 것과 같은 경로).
/// AUMID 는 셸의 AppsFolder 를 열거해 찾고, 찾으면 AppEntry.AumId 에 캐시한다.
/// </summary>
public static class AppLauncher
{
    /// <summary>앱을 실행한다. 성공적으로 실행 요청을 보냈으면 true.</summary>
    public static bool Launch(AppEntry app)
    {
        // 1) 이전에 찾아둔 AUMID 가 있으면 그게 가장 확실하다(패키지 앱 재활성화 포함).
        if (!string.IsNullOrEmpty(app.AumId) && LaunchByAumid(app.AumId)) return true;

        string? path = IconService.ResolvePath(app);
        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(app.ExePath)) path = app.ExePath;

        // 2) 패키지 앱 경로면 exe 를 직접 띄우지 않는다(권한 거부 + 패키지 컨텍스트 밖 실행).
        if (!string.IsNullOrEmpty(path) && IsPackagedPath(path))
        {
            string? aumid = FindAumid(app, path);
            if (aumid != null && LaunchByAumid(aumid)) { app.AumId = aumid; return true; }
        }

        // 3) 일반 exe.
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch { /* 아래 AppsFolder 폴백으로 */ }
        }

        // 4) 경로를 못 찾거나 실행이 막혔을 때의 마지막 폴백: 이름으로 AppsFolder 검색.
        string? found = FindAumid(app, path);
        if (found != null && LaunchByAumid(found)) { app.AumId = found; return true; }
        return false;
    }

    /// <summary>WindowsApps(스토어/MSIX) 아래 경로인지.</summary>
    public static bool IsPackagedPath(string path)
        => path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>AUMID 로 앱을 띄운다. explorer.exe 를 통해야 셸이 패키지를 활성화해 준다.</summary>
    private static bool LaunchByAumid(string aumid)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid)
            { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 셸의 AppsFolder(시작 메뉴의 "모든 앱"과 같은 목록)를 열거해 이 앱의 AUMID 를 찾는다.
    /// 우선순위: 패키지 패밀리 이름 일치 → 표시 이름 일치 → 실행 파일 이름 일치.
    /// </summary>
    private static string? FindAumid(AppEntry app, string? path)
    {
        string? family = path != null ? PackageFamilyFromPath(path) : null;
        string exeName = app.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? app.ProcessName[..^4] : app.ProcessName;

        string? byName = null, byExe = null;
        foreach (var (name, aumid) in EnumerateApps())
        {
            if (family != null && aumid.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase))
                return aumid;                                   // 가장 정확한 일치
            if (byName == null && !string.IsNullOrEmpty(app.Name)
                && string.Equals(name, app.Name, StringComparison.OrdinalIgnoreCase))
                byName = aumid;
            if (byExe == null && !string.IsNullOrEmpty(exeName)
                && string.Equals(name, exeName, StringComparison.OrdinalIgnoreCase))
                byExe = aumid;
        }
        return byName ?? byExe;
    }

    /// <summary>
    /// "…\WindowsApps\OpenAI.ChatGPT-Desktop_1.0.0.0_x64__2p2nqsd0c76g0\chatgpt.exe" 에서
    /// 패키지 패밀리 이름("OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0")을 뽑는다.
    /// 폴더명은 "이름_버전_아키텍처_리소스_게시자ID" 형식이다.
    /// </summary>
    private static string? PackageFamilyFromPath(string path)
    {
        try
        {
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!string.Equals(parts[i], "WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                string dir = parts[i + 1];
                int pub = dir.LastIndexOf("__", StringComparison.Ordinal);
                int first = dir.IndexOf('_');
                if (pub < 0 || first < 0 || first >= pub) return null;
                return dir[..first] + "_" + dir[(pub + 2)..];
            }
        }
        catch { }
        return null;
    }

    /// <summary>셸 AppsFolder 의 (표시 이름, AUMID) 목록. 셸 COM 이 막히면 빈 목록.</summary>
    private static IEnumerable<(string Name, string Aumid)> EnumerateApps()
    {
        var list = new List<(string, string)>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return list;
            dynamic? shell = Activator.CreateInstance(type);
            if (shell == null) return list;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder == null) return list;
            foreach (dynamic item in folder.Items())
            {
                try
                {
                    // AppsFolder 항목의 Path 는 AUMID 다.
                    string name = item.Name as string ?? "";
                    string aumid = item.Path as string ?? "";
                    if (aumid.Length > 0) list.Add((name, aumid));
                }
                catch { }
            }
        }
        catch { /* COM 불가 환경 → 폴백 없음 */ }
        return list;
    }
}
