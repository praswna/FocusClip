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
            catch
            {
                // 손상 시 빈 설정으로 진행하되, 손상 파일을 덮어쓰지 않는다(수동 복구 여지 보존).
                // 원자적 Save 도입으로 우리 쓰기로 인한 손상은 발생하지 않지만, 디스크 오류·외부 변조 대비.
                Config = new AppConfig();
                return;
            }
        }
        // 첫 실행(파일 없음)만 빈 등록 목록으로 시작하고 저장(설정 UI에서 직접 등록).
        Config = new AppConfig();
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
            // 원자적 저장: 임시 파일에 쓴 뒤 교체. 쓰는 도중 크래시/강제종료로 config.json이 잘려
            // 다음 실행에서 손상→빈 기본값으로 덮어써지며 등록 앱이 통째로 소실되는 것을 막는다.
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, true);
        }
        catch { }
    }

}
