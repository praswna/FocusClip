using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using FocusClip.Models;

namespace FocusClip.Services;

/// <summary>
/// 사용자가 직접 등록·관리하는 프롬프트 보관함. 그룹(=팝업 창) 단위로 묶이며 prompts.json 한 파일에
/// 전량 영구 저장한다(그룹 이름·순서·핀 위치·항목이 한 객체 — 파일명 변환/인덱스 불일치 문제 없음).
/// 구 형식(평면 배열)은 로드 시 "기본" 그룹으로 자동 이전한다.
/// </summary>
public sealed class PromptService
{
    private static readonly string StorePath = Path.Combine(ConfigService.Dir, "prompts.json");

    /// <summary>그룹 이름이 비어 있을 때 쓰는 기본 그룹명(구 형식 이전 대상이기도 함).</summary>
    public const string DefaultGroupName = "기본";

    public ObservableCollection<PromptGroup> Groups { get; } = new();

    /// <summary>마지막으로 저장(추가/이동)한 그룹 이름. 승격·추가 대화상자의 기본값이자 오버레이 대표 팝업.</summary>
    public string LastGroup { get; private set; } = "";

    // 직렬화용 레코드 (System.Text.Json이 생성자 파라미터로 매핑)
    private record PromptRecord(string? Title, string? Text);
    private record GroupRecord(string? Name, bool Pinned, double? PinX, double? PinY, List<PromptRecord>? Items);
    private record StoreRecord(string? LastGroup, List<GroupRecord>? Groups);

    /// <summary>시작 시 prompts.json에서 전체 그룹·프롬프트 복원. 구(평면 배열) 형식이면 "기본" 그룹으로 이전.</summary>
    public void Load()
    {
        if (!File.Exists(StorePath)) return;
        try
        {
            var json = File.ReadAllText(StorePath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            if (json.TrimStart().StartsWith("["))
            {
                // 구 형식: [{Title,Text},...] → "기본" 그룹 하나로 감싸고 새 형식으로 재기록
                var records = JsonSerializer.Deserialize<List<PromptRecord>>(json, opts);
                if (records == null || records.Count == 0) return;
                var g = new PromptGroup { Name = DefaultGroupName };
                foreach (var r in records)
                    g.Items.Add(new PromptItem { Title = r.Title ?? "", Text = r.Text ?? "" });
                Groups.Add(g);
                LastGroup = g.Name;
                Save();
                return;
            }

            var store = JsonSerializer.Deserialize<StoreRecord>(json, opts);
            if (store?.Groups == null) return;
            foreach (var gr in store.Groups)
            {
                if (string.IsNullOrWhiteSpace(gr.Name)) continue;
                var g = new PromptGroup
                {
                    Name = gr.Name.Trim(),
                    Pinned = gr.Pinned,
                    PinX = gr.PinX ?? double.NaN,
                    PinY = gr.PinY ?? double.NaN,
                };
                if (gr.Items != null)
                    foreach (var r in gr.Items)
                        g.Items.Add(new PromptItem { Title = r.Title ?? "", Text = r.Text ?? "" });
                Groups.Add(g);
            }
            LastGroup = store.LastGroup ?? "";
        }
        catch { /* 손상 시 빈 보관함으로 시작(파일은 다음 저장에서 재생성) */ }
    }

    /// <summary>이름으로 그룹 찾기(대소문자 무시, 앞뒤 공백 무시). 없으면 null.</summary>
    public PromptGroup? FindGroup(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) return null;
        return Groups.FirstOrDefault(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>이름으로 그룹을 찾고 없으면 새로 만든다(맨 뒤에 추가). 빈 이름은 기본 그룹명으로.</summary>
    public PromptGroup GetOrCreateGroup(string? name)
    {
        string n = string.IsNullOrWhiteSpace(name) ? DefaultGroupName : name!.Trim();
        var g = FindGroup(n);
        if (g == null)
        {
            g = new PromptGroup { Name = n };
            Groups.Add(g);
        }
        return g;
    }

    /// <summary>항목이 속한 그룹. (그룹에서 제거된 직후라면 null)</summary>
    public PromptGroup? GroupOf(PromptItem item) => Groups.FirstOrDefault(g => g.Items.Contains(item));

    /// <summary>추가/승격 대화상자의 기본 그룹명: 마지막 사용 그룹 → 첫 그룹 → 기본 그룹명.</summary>
    public string LastGroupNameOrDefault
        => FindGroup(LastGroup)?.Name ?? Groups.FirstOrDefault()?.Name ?? DefaultGroupName;

    /// <summary>오버레이에 표시할 대표 그룹: 마지막 사용 그룹(비어 있지 않으면), 아니면 항목이 있는 첫 그룹.</summary>
    public PromptGroup? RepresentativeGroup()
    {
        var last = FindGroup(LastGroup);
        if (last != null && last.Items.Count > 0) return last;
        return Groups.FirstOrDefault(g => g.Items.Count > 0);
    }

    /// <summary>새 프롬프트를 지정 그룹(없으면 생성) 맨 위에 추가하고 저장. 그 그룹이 마지막 사용 그룹이 된다.</summary>
    public void Add(string groupName, string title, string text)
    {
        var g = GetOrCreateGroup(groupName);
        g.Items.Insert(0, new PromptItem { Title = title ?? "", Text = text ?? "" });
        LastGroup = g.Name;
        Save();
    }

    /// <summary>기존 프롬프트의 제목·본문 교체 + 그룹이 바뀌었으면 이동(대상 그룹 맨 위) 후 저장.</summary>
    public void Update(PromptItem item, string title, string text, string groupName)
    {
        item.Title = title ?? "";
        item.Text = text ?? "";
        var cur = GroupOf(item);
        var target = GetOrCreateGroup(groupName);
        if (cur != null && !ReferenceEquals(cur, target))
        {
            cur.Items.Remove(item);
            target.Items.Insert(0, item);
        }
        LastGroup = target.Name;
        Save();
    }

    /// <summary>프롬프트 제거 후 저장. 빈 그룹은 남긴다(핀 위치 유지, 삭제는 그룹 메뉴에서 명시적으로).</summary>
    public void Remove(PromptItem item)
    {
        var g = GroupOf(item);
        if (g != null && g.Items.Remove(item)) Save();
    }

    /// <summary>그룹 제거 후 저장(호출자가 빈 그룹인지 확인). 마지막 사용 그룹이었으면 다른 그룹으로 넘긴다.</summary>
    public void RemoveGroup(PromptGroup g)
    {
        if (!Groups.Remove(g)) return;
        if (string.Equals(LastGroup, g.Name, StringComparison.OrdinalIgnoreCase))
            LastGroup = Groups.FirstOrDefault()?.Name ?? "";
        Save();
    }

    /// <summary>그룹 전환 시 마지막 사용 그룹 갱신(다음 오버레이의 대표 팝업).</summary>
    public void SetLastGroup(string name)
    {
        LastGroup = (name ?? "").Trim();
        Save();
    }

    /// <summary>현재 상태를 prompts.json으로 원자적 저장(임시파일→교체, 부분 쓰기 손상 방지).
    /// 핀 위치 갱신처럼 외부에서 그룹 속성을 바꾼 뒤에도 호출한다.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigService.Dir);
            var groups = Groups.Select(g => new GroupRecord(
                g.Name, g.Pinned,
                double.IsNaN(g.PinX) ? null : g.PinX,
                double.IsNaN(g.PinY) ? null : g.PinY,
                g.Items.Select(p => new PromptRecord(p.Title, p.Text)).ToList())).ToList();
            string json = JsonSerializer.Serialize(new StoreRecord(LastGroup, groups),
                new JsonSerializerOptions { WriteIndented = true });
            string tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, StorePath, true);
        }
        catch { }
    }
}
