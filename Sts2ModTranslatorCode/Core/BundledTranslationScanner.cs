using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sts2ModTranslator.Core;

/// <summary>설치된 "번역 모드" 한 개의 메타데이터(리포트/UI 표시용).</summary>
public sealed class TranslationProvider
{
    public string Id = "";
    public string Name = "";

    /// <summary>이 번역 모드가 번역하는 대상 모드 id 목록.</summary>
    public List<string> Targets = new();

    /// <summary>이 번역 모드가 제공하는 언어 코드 목록(예: kor, jpn).</summary>
    public List<string> Langs = new();

    /// <summary>제공하는 비어 있지 않은 번역 키의 총합(모든 언어/테이블 합산).</summary>
    public int Keys;
}

/// <summary>
/// 부팅 시 자동 감지된 번역 모드들의 번역을 한곳에 모은 집계본.
///
/// 조회 경로: 대상모드 → 언어 → 테이블 → (loc key → 번역값). 값이 비어 있는 항목은 담지 않는다.
/// 같은 (대상,언어,테이블,키)를 여러 번역 모드가 제공하면 먼저 적재된 쪽을 유지한다.
/// </summary>
public sealed class BundledTranslations
{
    private static readonly Dictionary<string, string> Empty = new();

    /// <summary>대상모드 id → 언어 → 테이블 → (키 → 값).</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, string>>>> ByTarget
        = new(StringComparer.Ordinal);

    /// <summary>감지된 번역 모드 목록(리포트/UI 표시용).</summary>
    public List<TranslationProvider> Providers = new();

    public bool Any => Providers.Count > 0;

    /// <summary>해당 대상모드를 번역하는 번역 모드가 하나라도 있으면 true.</summary>
    public bool HasTarget(string targetId) =>
        ByTarget.TryGetValue(targetId, out var byLang) && byLang.Count > 0;

    /// <summary>해당 대상모드에 대해 설치된 팩이 제공하는 언어 코드들(정렬). 없으면 빈 목록.</summary>
    public List<string> LangsForTarget(string targetId) =>
        ByTarget.TryGetValue(targetId, out var byLang)
            ? byLang.Keys.OrderBy(s => s, StringComparer.Ordinal).ToList()
            : new List<string>();

    /// <summary>주어진 (대상, 언어)의 테이블맵. 없으면 null.</summary>
    public Dictionary<string, Dictionary<string, string>>? ForTargetLang(string targetId, string lang)
    {
        if (ByTarget.TryGetValue(targetId, out var byLang) && byLang.TryGetValue(lang, out var byTable))
            return byTable;
        return null;
    }

    /// <summary>주어진 (대상, 언어, 테이블)의 (키→값). 없으면 빈 dict.</summary>
    public Dictionary<string, string> ForTable(string targetId, string lang, string table)
    {
        var byTable = ForTargetLang(targetId, lang);
        if (byTable != null && byTable.TryGetValue(table, out var d)) return d;
        return Empty;
    }

    internal void Add(string targetId, string lang, string table, Dictionary<string, string> values)
    {
        if (values.Count == 0) return;
        if (!ByTarget.TryGetValue(targetId, out var byLang))
            ByTarget[targetId] = byLang = new(StringComparer.Ordinal);
        if (!byLang.TryGetValue(lang, out var byTable))
            byLang[lang] = byTable = new(StringComparer.Ordinal);
        if (!byTable.TryGetValue(table, out var dict))
            byTable[table] = dict = new(StringComparer.Ordinal);
        foreach (var kv in values)
            if (!dict.ContainsKey(kv.Key)) dict[kv.Key] = kv.Value; // 선 적재 우선
    }
}

/// <summary>
/// 로드된 모드 중 번역 모드(= Sts2ModTranslator 를 참조해 번역 JSON 을 동봉한 모드)를 읽는다.
///
/// 규약 레이아웃 (게임의 res:// 경로 위):
///   res://{번역모드id}/translations/{대상모드id}/{lang}/{table}.txt   ({string:string} JSON 내용)
///
/// 파일 확장자는 .txt(<see cref="TranslationStore.DataExt"/>) — .json 으로 두면 ModManager 가
/// 매니페스트로 파싱 시도해 'missing id' 로그를 남기기 때문. 구버전 .json 데이터도 함께 읽는다.
/// 이 경로만 약속하면 별도 DLL 없이 순수 데이터 모드로 번역을 배포할 수 있다.
///
/// ★읽기 경로 = 모드의 실제 디스크 폴더(<c>Mod.path</c>). 번역팩은 PCK/DLL 이 없는 loose-file
/// 데이터 모드인데, STS2 는 PCK 없는 모드의 파일을 <c>res://</c> 에 마운트하지 않으므로
/// res:// 로는 읽을 수 없다(로그 "Neither a DLL nor a PCK was loaded"). 따라서 파일시스템으로 읽는다.
/// PCK 안에 translations/ 를 담은 제공 모드 대비로 res:// 폴백도 둔다.
/// </summary>
public static class BundledTranslationScanner
{
    public const string FolderName = "translations";

    /// <summary>
    /// 한 모드의 translations/ 폴더를 읽어 agg 에 적재. 비어 있지 않은 번역을 하나라도 담았으면 true.
    /// modPath = 모드의 실제 디스크 경로(<c>Mod.path</c>). 1순위로 파일시스템을, 없으면 res:// 를 읽는다.
    /// (false = 번역 모드가 아니거나 내용이 비어 있음)
    /// </summary>
    public static bool TryRead(string id, string name, string? modPath, BundledTranslations agg)
    {
        var provider = new TranslationProvider { Id = id, Name = name };

        // 두 경로를 모두 읽는다(SkinManager 패턴). 같은 (대상,언어,테이블,키)는 BundledTranslations.Add
        // 가 선적재 우선으로 dedup 하므로 중복 적재돼도 안전하다.
        //  ① 실제 디스크 경로 — 번역팩은 PCK/DLL 없는 loose-file 데이터 모드라 res:// 에 마운트되지 않음.
        //  ② res:// — translations/ 를 PCK 안에 담은 제공 모드 대비.
        string? fsRoot = string.IsNullOrEmpty(modPath) ? null : Path.Combine(modPath!, FolderName);
        if (fsRoot != null && Directory.Exists(fsRoot))
            ReadFs(fsRoot, agg, provider);
        ReadRes($"res://{id}/{FolderName}", agg, provider);

        if (provider.Keys == 0) return false; // translations/ 는 있으나 실질 번역 없음
        provider.Targets.Sort(StringComparer.Ordinal);
        provider.Langs.Sort(StringComparer.Ordinal);
        agg.Providers.Add(provider);
        return true;
    }

    private static bool IsDataFile(string f) =>
        f.EndsWith(TranslationStore.DataExt, StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal);

    private static string TableOf(string file)
    {
        int dot = file.LastIndexOf('.');
        return dot > 0 ? file.Substring(0, dot) : file;
    }

    private static void Aggregate(BundledTranslations agg, TranslationProvider p,
        string targetId, string lang, string table, Dictionary<string, string> dict)
    {
        var nonEmpty = dict.Where(kv => !string.IsNullOrEmpty(kv.Value))
                           .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (nonEmpty.Count == 0) return;
        agg.Add(targetId, lang, table, nonEmpty);
        p.Keys += nonEmpty.Count;
        if (!p.Targets.Contains(targetId)) p.Targets.Add(targetId);
        if (!p.Langs.Contains(lang)) p.Langs.Add(lang);
    }

    // ── 파일시스템 읽기(기본) ──────────────────────────────
    private static void ReadFs(string root, BundledTranslations agg, TranslationProvider p)
    {
        foreach (string tdir in SafeDirsFs(root))
        {
            string targetId = Path.GetFileName(tdir);
            foreach (string ldir in SafeDirsFs(tdir))
            {
                string lang = Path.GetFileName(ldir);
                foreach (string fpath in SafeFilesFs(ldir).Where(f => IsDataFile(Path.GetFileName(f))))
                    Aggregate(agg, p, targetId, lang, TableOf(Path.GetFileName(fpath)), ReadFsJson(fpath));
            }
        }
    }

    private static List<string> SafeDirsFs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir).ToList() : new(); }
        catch { return new(); }
    }

    private static List<string> SafeFilesFs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir).ToList() : new(); }
        catch { return new(); }
    }

    private static Dictionary<string, string> ReadFsJson(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new();
        }
        catch { return new(); }
    }

    // ── res:// 읽기(PCK 패키징 제공 모드 폴백) ─────────────
    private static void ReadRes(string root, BundledTranslations agg, TranslationProvider p)
    {
        if (!Godot.DirAccess.DirExistsAbsolute(root)) return;
        foreach (string targetId in ModLocScanner.SafeDirs(root))
        {
            string tdir = $"{root}/{targetId}";
            foreach (string lang in ModLocScanner.SafeDirs(tdir))
            {
                string ldir = $"{tdir}/{lang}";
                foreach (string file in ModLocScanner.SafeFiles(ldir).Where(IsDataFile))
                    Aggregate(agg, p, targetId, lang, TableOf(file), ModLocScanner.ReadResJson($"{ldir}/{file}"));
            }
        }
    }
}
