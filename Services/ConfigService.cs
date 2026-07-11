using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FocusClip.Models;

namespace FocusClip.Services;

/// <summary>
/// %LOCALAPPDATA%\FocusClip\config.json 로드/저장. 최초 실행 시 빈 설정으로 시작한다.
/// 모든 앱 데이터(config·clips·icons·media)를 이 한 폴더 아래에 둔다(OneDrive·로밍 비대상).
/// </summary>
public sealed class ConfigService
{
    // 사용자 파일(설정·기록·프롬프트·아이콘 캐시)은 사진\Screenshots\FocusClip 에 둔다.
    // (클립 본문 txt/png 은 그 상위 Screenshots 루트에 직접 저장 — ClipboardService.SaveDir)
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots", "FocusClip");

    static ConfigService()
    {
        // 구 위치에서 새 위치(사진\Screenshots\FocusClip)로 데이터 이전.
        // 최신 데이터가 있는 LocalAppData\FocusClip 을 먼저(새 위치에 없는 항목만 복사),
        // 더 오래된 Roaming(%APPDATA%)\FocusClip 이 남은 빈 항목을 채운다. 옛 폴더는 그대로 둔다.
        TryMigrateFrom(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusClip"));
        TryMigrateFrom(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusClip"));
    }

    /// <summary>구 위치(old)의 사용자 파일을 새 Dir로 이전. 파일 단위로, 새 위치에 없는 항목만 복사한다
    /// — 이미 일부 있어도 누락분(config·clips·prompts·icons)을 채운다. 옛 폴더는 백업으로 남겨 둔다.
    /// 클립 본문(txt/png)은 clips.json에 절대경로로 남아 옛 위치에서 그대로 로드되므로 이전하지 않는다.</summary>
    private static void TryMigrateFrom(string old)
    {
        try
        {
            if (!Directory.Exists(old) || string.Equals(old, Dir, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Dir);

            foreach (var name in new[] { "config.json", "config.bak", "clips.json", "prompts.json" })
            {
                string src = Path.Combine(old, name);
                string dst = Path.Combine(Dir, name);
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
            }

            string oldIcons = Path.Combine(old, "icons");
            string newIcons = Path.Combine(Dir, "icons");
            if (Directory.Exists(oldIcons) && !Directory.Exists(newIcons))
            {
                Directory.CreateDirectory(newIcons);
                foreach (var f in Directory.GetFiles(oldIcons))
                    File.Copy(f, Path.Combine(newIcons, Path.GetFileName(f)), true);
            }
        }
        catch { /* 이전 실패 시 빈 설정으로 시작(데이터 유실 아님 — 옛 폴더는 그대로 남음) */ }
    }

    private static string ConfigPath => Path.Combine(Dir, "config.json");
    private static string BakPath => Path.Combine(Dir, "config.bak"); // 마지막 '비어있지 않은' 정상 설정 백업

    public AppConfig Config { get; private set; } = new();

    public void Load()
    {
        Directory.CreateDirectory(Dir);

        // 1) 정상 config.json (유효 파싱이면 빈 목록이어도 사용자 의도로 존중).
        if (TryLoadFrom(ConfigPath)) return;

        // 2) config.json이 손상/없음 → 마지막 정상 백업(config.bak)에서 복구하고 config.json 재생성.
        if (TryLoadFrom(BakPath)) { Save(); return; }

        // 3) 둘 다 없음(첫 실행) → 빈 설정. 단, config.json이 '존재하지만 손상'이면 덮어쓰지 않는다(수동 복구 여지 보존).
        Config = new AppConfig();
        if (!File.Exists(ConfigPath)) Save();
    }

    private bool TryLoadFrom(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
            if (c == null) return false;
            Config = c;
            return true;
        }
        catch { return false; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
            WriteAtomic(ConfigPath, json);
            // 비어있지 않은 설정만 백업으로 보존 — 사고로 빈 설정이 저장돼도 백업은 마지막 정상본을 유지해
            // 다음 시작 시 복구할 수 있게 한다(반복적 앱 소실 방지).
            if (Config.Apps.Count > 0) WriteAtomic(BakPath, json);
        }
        catch { }
    }

    /// <summary>임시 파일에 쓴 뒤 교체. 쓰는 도중 크래시/강제종료로 파일이 잘려 손상되는 것을 막는다(원자적 저장).</summary>
    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, true);
    }
}
