using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FocusClip.Models;

namespace FocusClip.Services;

/// <summary>
/// %APPDATA%\FocusClip\config.json 로드/저장. 최초 실행 시 기존 FocusManager의
/// config.ini 를 발견하면 1회 이관한다.
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
            catch { /* 손상 시 이관/기본값으로 진행 */ }
        }
        Config = TryMigrateFromFocusManager() ?? new AppConfig();
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

    private static AppConfig? TryMigrateFromFocusManager()
    {
        // 알려진 FocusManager config.ini 위치. 없으면 빈 설정으로 시작(설정 UI에서 등록).
        string[] candidates =
        {
            @"C:\Users\prasw\Dropbox\Cluade\FocusManager\FocusManager\config.ini",
        };
        foreach (var ini in candidates)
            if (File.Exists(ini))
                return ParseIni(ini);
        return null;
    }

    private static AppConfig ParseIni(string path)
    {
        var cfg = new AppConfig();
        string section = "";
        foreach (var raw in File.ReadAllLines(path)) // UTF-16 BOM 자동 감지
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line.Trim('[', ']'); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();

            if (section.Equals("Settings", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("PinnedCount", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int pc))
                    cfg.PinnedCount = pc;
            }
            else if (section.Equals("Apps", StringComparison.OrdinalIgnoreCase))
            {
                var parts = val.Split('|');   // "ahk_exe proc.exe|C:\path"
                if (parts.Length >= 2)
                {
                    string proc = Regex.Replace(parts[0].Trim(), @"(?i)^ahk_exe\s+", "").Trim();
                    cfg.Apps.Add(new AppEntry { Name = key, ProcessName = proc, ExePath = parts[1].Trim() });
                }
            }
        }
        return cfg;
    }
}
