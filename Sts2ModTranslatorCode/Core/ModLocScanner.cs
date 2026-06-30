using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2ModTranslator.Core;

/// <summary>지원 모드 한 개의 추출된 로컬라이제이션 정보.</summary>
public sealed class SupportedMod
{
    public string Id = "";
    public string Name = "";
    public string Version = "";

    /// <summary>모드가 동봉한 언어 폴더 목록 (e.g. eng, esp, zhs).</summary>
    public List<string> ShipsLangs = new();

    /// <summary>모드가 동봉한 모든 언어: lang → 테이블명 → (loc key → 텍스트).</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> ByLang = new();

    /// <summary>eng(기준 언어) 테이블. 키 집합/템플릿의 단일 출처.</summary>
    public Dictionary<string, Dictionary<string, string>> EngByTable =>
        ByLang.TryGetValue("eng", out var d) ? d : new();

    public int TotalKeys => EngByTable.Values.Sum(d => d.Count);
}

/// <summary>미지원 모드 한 개 + 사유.</summary>
public sealed class UnsupportedMod
{
    public string Id = "";
    public string Name = "";
    public string Reason = "";
}

public sealed class ScanResult
{
    public List<SupportedMod> Supported = new();
    public List<UnsupportedMod> Unsupported = new();

    /// <summary>
    /// 설치된 "번역 모드"(Sts2ModTranslator 를 참조해 번역 JSON 을 동봉한 모드)가 제공하는
    /// 번역의 집계본. 부팅 시 자동 감지되어 런타임 주입에 사용된다.
    /// </summary>
    public BundledTranslations Bundled = new();
}

/// <summary>
/// 로드된 모드를 게임의 로크 경로 규약(res://{id}/localization/{lang}/{file})으로 스캔.
/// localization 폴더 + eng 테이블을 동봉한 모드만 "지원" 으로 분류한다.
/// </summary>
public static class ModLocScanner
{
    private const string SourceLang = "eng"; // 원문 기준 언어 (모든 모드가 eng 를 베이스로 동봉)

    public static ScanResult Scan()
    {
        var result = new ScanResult();

        foreach (var mod in ModManager.GetLoadedMods())
        {
            string id = mod.manifest?.id ?? "";
            string name = mod.manifest?.name ?? id;
            string version = mod.manifest?.version ?? "";
            if (string.IsNullOrEmpty(id)) continue;
            if (id == MainFile.ModId) continue; // 자기 자신 제외

            // 이 모드가 "번역 모드"(translations/ 동봉)인지 먼저 본다. 번역 JSON 을 모아 두고,
            // localization/ 이 없더라도 미지원으로 분류하지 않는다(번역 모드는 원래 localization 이 없음).
            bool isProvider = BundledTranslationScanner.TryRead(id, name, mod.path, result.Bundled);

            string locRoot = $"res://{id}/localization";
            if (!Godot.DirAccess.DirExistsAbsolute(locRoot))
            {
                if (isProvider) continue; // 번역 모드 — 미지원 아님
                result.Unsupported.Add(new UnsupportedMod
                {
                    Id = id, Name = name,
                    Reason = "no localization/ folder (uses runtime-merge or hardcoded text)"
                });
                continue;
            }

            var langs = SafeDirs(locRoot);
            var sm = new SupportedMod { Id = id, Name = name, Version = version, ShipsLangs = langs };

            // 동봉된 모든 언어를 읽는다 (번역 참조용). eng 는 키 집합의 기준.
            foreach (string lang in langs)
            {
                string ldir = $"{locRoot}/{lang}";
                var tables = new Dictionary<string, Dictionary<string, string>>();
                foreach (string file in SafeFiles(ldir).Where(f => f.EndsWith(".json")))
                {
                    string table = file.Substring(0, file.Length - ".json".Length);
                    var dict = ReadResJson($"{ldir}/{file}");
                    if (dict.Count > 0) tables[table] = dict;
                }
                if (tables.Count > 0) sm.ByLang[lang] = tables;
            }

            if (!sm.ByLang.ContainsKey(SourceLang) || sm.EngByTable.Count == 0)
            {
                result.Unsupported.Add(new UnsupportedMod
                {
                    Id = id, Name = name,
                    Reason = $"localization/ present but no readable '{SourceLang}' source tables"
                });
                continue;
            }

            result.Supported.Add(sm);
        }

        return result;
    }

    // ── Godot res:// 헬퍼 ───────────────────────────────────────

    internal static List<string> SafeDirs(string resDir)
    {
        try { return Godot.DirAccess.GetDirectoriesAt(resDir).Where(s => !string.IsNullOrEmpty(s)).ToList(); }
        catch { return new(); }
    }

    internal static List<string> SafeFiles(string resDir)
    {
        try { return Godot.DirAccess.GetFilesAt(resDir).Where(s => !string.IsNullOrEmpty(s)).ToList(); }
        catch { return new(); }
    }

    /// <summary>res:// (pck 포함) 경로의 flat {string:string} JSON 을 읽는다. 실패 시 빈 dict.</summary>
    public static Dictionary<string, string> ReadResJson(string resPath)
    {
        try
        {
            using var f = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
            if (f == null) return new();
            string text = f.GetAsText();
            if (string.IsNullOrWhiteSpace(text)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
