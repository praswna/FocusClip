using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using FocusClip.Models;

namespace FocusClip.Services;

/// <summary>
/// 사용자가 직접 등록·관리하는 프롬프트 보관함. prompts.json에 전량 영구 저장한다
/// (클립보드의 핀 기반 저장과 달리, 보관함이므로 항상 저장).
/// </summary>
public sealed class PromptService
{
    private static readonly string StorePath = Path.Combine(ConfigService.Dir, "prompts.json");

    public ObservableCollection<PromptItem> Prompts { get; } = new();

    // 직렬화용 레코드 (System.Text.Json이 생성자 파라미터로 매핑)
    private record PromptRecord(string Title, string Text);

    /// <summary>시작 시 prompts.json에서 전체 프롬프트 복원.</summary>
    public void Load()
    {
        if (!File.Exists(StorePath)) return;
        try
        {
            var json = File.ReadAllText(StorePath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var records = JsonSerializer.Deserialize<List<PromptRecord>>(json, opts);
            if (records == null) return;
            foreach (var r in records)
                Prompts.Add(new PromptItem { Title = r.Title ?? "", Text = r.Text ?? "" });
        }
        catch { /* 손상 시 빈 보관함으로 시작(파일은 다음 저장에서 재생성) */ }
    }

    /// <summary>새 프롬프트를 맨 위에 추가하고 저장.</summary>
    public void Add(string title, string text)
    {
        Prompts.Insert(0, new PromptItem { Title = title ?? "", Text = text ?? "" });
        Save();
    }

    /// <summary>기존 프롬프트의 제목·본문 교체 후 저장.</summary>
    public void Update(PromptItem item, string title, string text)
    {
        item.Title = title ?? "";
        item.Text = text ?? "";
        Save();
    }

    /// <summary>프롬프트 제거 후 저장.</summary>
    public void Remove(PromptItem item)
    {
        if (Prompts.Remove(item)) Save();
    }

    /// <summary>목록 순서 변경 후 저장(재정렬용, 현재 UI 미사용).</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Prompts.Count || to < 0 || to >= Prompts.Count || from == to) return;
        Prompts.Move(from, to);
        Save();
    }

    /// <summary>현재 목록을 prompts.json으로 원자적 저장(임시파일→교체, 부분 쓰기 손상 방지).</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigService.Dir);
            var records = Prompts.Select(p => new PromptRecord(p.Title, p.Text)).ToList();
            string json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            string tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, StorePath, true);
        }
        catch { }
    }
}
