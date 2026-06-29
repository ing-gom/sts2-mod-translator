using System;
using System.Collections.Generic;
using System.Linq;

namespace Sts2ModTranslator.Core;

/// <summary>설치된 "번역 모드" 한 개의 메타데이터(리포트/UI 표시용).</summary>
public sealed class TranslationProvider
{
    public string Id = "";
    public string Name = "";

    /// <summary>이 번역 모드가 번역하는 대상 모드 id 목록.</summary>
    public List<string> Targets = new();

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
/// 이 경로만 약속하면 별도 DLL 없이 순수 데이터 모드로 번역을 배포할 수 있다(baselib 데이터 모드와 동일).
/// </summary>
public static class BundledTranslationScanner
{
    public const string FolderName = "translations";

    /// <summary>
    /// 한 모드의 translations/ 폴더를 읽어 agg 에 적재. 비어 있지 않은 번역을 하나라도 담았으면 true.
    /// (false = 번역 모드가 아니거나 내용이 비어 있음)
    /// </summary>
    public static bool TryRead(string id, string name, BundledTranslations agg)
    {
        string root = $"res://{id}/{FolderName}";
        if (!Godot.DirAccess.DirExistsAbsolute(root)) return false;

        var provider = new TranslationProvider { Id = id, Name = name };
        foreach (string targetId in ModLocScanner.SafeDirs(root))
        {
            string tdir = $"{root}/{targetId}";
            foreach (string lang in ModLocScanner.SafeDirs(tdir))
            {
                string ldir = $"{tdir}/{lang}";
                foreach (string file in ModLocScanner.SafeFiles(ldir)
                             .Where(f => f.EndsWith(TranslationStore.DataExt) || f.EndsWith(".json")))
                {
                    int dot = file.LastIndexOf('.');
                    string table = dot > 0 ? file.Substring(0, dot) : file;
                    var dict = ModLocScanner.ReadResJson($"{ldir}/{file}");
                    var nonEmpty = dict.Where(kv => !string.IsNullOrEmpty(kv.Value))
                                       .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                    if (nonEmpty.Count == 0) continue;

                    agg.Add(targetId, lang, table, nonEmpty);
                    provider.Keys += nonEmpty.Count;
                    if (!provider.Targets.Contains(targetId)) provider.Targets.Add(targetId);
                }
            }
        }

        if (provider.Keys == 0) return false; // translations/ 는 있으나 실질 번역 없음
        provider.Targets.Sort(StringComparer.Ordinal);
        agg.Providers.Add(provider);
        return true;
    }
}
