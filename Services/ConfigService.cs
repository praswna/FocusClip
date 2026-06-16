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
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusClip");

    static ConfigService() => TryMigrateFromRoaming();

    /// <summary>구버전이 쓰던 %APPDATA%(Roaming)\FocusClip 데이터를 새 Local 위치로 이전.
    /// 파일 단위로, 새 위치에 없는 항목만 복사한다 — 새 폴더가 media 등으로 이미 존재해도
    /// 누락분(config·clips·icons)을 채운다. 옛 폴더는 백업으로 남겨 둔다.</summary>
    private static void TryMigrateFromRoaming()
    {
        try
        {
            string old = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusClip");
            if (!Directory.Exists(old)) return;
            Directory.CreateDirectory(Dir);

            foreach (var name in new[] { "config.json", "clips.json" })
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

    public AppConfig Config { get; private set; } = new();

    public void Load()
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(ConfigPath))
        {
            try
            {
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
                return;
            }
            catch { /* 손상 시 기본값으로 진행 */ }
        }
        // 첫 실행은 빈 등록 목록으로 시작(설정 UI에서 직접 등록).
        Config = new AppConfig();
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

}
