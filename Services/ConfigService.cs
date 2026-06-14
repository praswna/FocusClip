using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FocusClip.Models;

namespace FocusClip.Services;

/// <summary>
/// %APPDATA%\FocusClip\config.json 로드/저장. 최초 실행 시 빈 설정으로 시작한다.
/// </summary>
public sealed class ConfigService
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusClip");

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
