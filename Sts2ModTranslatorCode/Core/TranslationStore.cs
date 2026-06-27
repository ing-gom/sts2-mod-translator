using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sts2ModTranslator.Core;

/// <summary>
/// %APPDATA%\Sts2ModTranslator\ 의 파일 입출력 담당.
///
/// 레이아웃:
///   source/{id}/eng/{table}.json        — 원문(영어) 참조본. 매번 새로 씀(읽기 전용 취급).
///   overrides/{id}/{lang}/{table}.json   — 번역 입력칸. 값이 비어 있으면 미번역(원문 fallback).
///                                          기존 파일은 절대 덮어쓰지 않고, 새 키만 추가한다.
///   supported_mods.json                  — 지원/미지원 모드 목록 + 진행률 리포트.
/// </summary>
public static class TranslationStore
{
    private static string? _rootCache;

    /// <summary>
    /// 번역 데이터 루트. 1순위 = 모드 폴더 내부의 Translations\ (DLL 옆),
    /// 쓰기 불가(예: Program Files 권한) 시 2순위 = %APPDATA%\Sts2ModTranslator\.
    ///
    /// 주의: STS2 ModManager 는 mods\ 트리의 모든 .json 을 매니페스트로 파싱 시도하므로
    /// 여기 생기는 *.json 에 대해 부팅 시 무해한 "missing id" 로그가 남을 수 있다(동작엔 영향 없음).
    /// </summary>
    public static string Root
    {
        get
        {
            if (_rootCache != null) return _rootCache;

            // 모드 자신의 폴더 = 이 DLL 이 있는 디렉터리 (SkinManager 와 동일 패턴).
            string? modDir = null;
            try
            {
                string loc = typeof(TranslationStore).Assembly.Location;
                if (!string.IsNullOrEmpty(loc)) modDir = Path.GetDirectoryName(loc);
            }
            catch { /* Location 비어있는 로드 컨텍스트 → 폴백 */ }

            string chosen;
            string inMod = modDir != null ? Path.Combine(modDir, "Translations") : "";
            if (!string.IsNullOrEmpty(inMod) && TryEnsureWritable(inMod))
            {
                chosen = inMod;
            }
            else
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                chosen = Path.Combine(appData, "Sts2ModTranslator");
                Directory.CreateDirectory(chosen);
            }

            _rootCache = chosen;
            return chosen;
        }
    }

    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        // 한국어/중국어 등 비ASCII 를 \uXXXX 로 이스케이프하지 않아 번역자가 그대로 편집 가능.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string SourceDir(string id, string lang) =>
        Path.Combine(Root, "source", id, lang);

    private static string OverrideDir(string id, string lang) =>
        Path.Combine(Root, "overrides", id, lang);

    public static string OverridePath(string id, string lang, string table) =>
        Path.Combine(OverrideDir(id, lang), table + ".json");

    /// <summary>
    /// 한 모드의 source(원문) 갱신 + override(번역칸) 템플릿 생성/증분.
    /// 기존 번역값은 보존하고, 모드 신규 키만 빈 값으로 추가한다.
    /// </summary>
    public static void EnsureTemplates(SupportedMod mod, string lang)
    {
        // 1) 원문 참조본: 모드가 동봉한 *모든* 언어를 기록(eng/esp/zhs…).
        //    한국어 번역 시 기존 중문/스페인어를 참고할 수 있게 한다. (항상 최신으로 덮어씀)
        foreach (var (refLang, tables) in mod.ByLang)
        {
            string srcDir = SourceDir(mod.Id, refLang);
            Directory.CreateDirectory(srcDir);
            foreach (var (table, dict) in tables)
                WriteJson(Path.Combine(srcDir, table + ".json"), Sorted(dict));
        }

        // 2) 번역 입력칸: eng 키 기준, 기존 번역 보존 + 신규 키만 빈 값으로 추가.
        foreach (var (table, engDict) in mod.EngByTable)
        {
            string ovrPath = OverridePath(mod.Id, lang, table);
            var existing = ReadJson(ovrPath);
            var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in engDict.Keys)
                merged[key] = existing.TryGetValue(key, out var v) ? v : ""; // 빈 값 = 미번역
            Directory.CreateDirectory(OverrideDir(mod.Id, lang));
            WriteJson(ovrPath, merged);
        }
    }

    /// <summary>주어진 모드/언어의 (총 키 수, 번역된 키 수). 진행률 표시용.</summary>
    public static (int total, int translated) Coverage(SupportedMod mod, string lang)
    {
        int total = mod.TotalKeys;
        int tr = LoadNonEmptyOverrides(mod, lang).Values.Sum(d => d.Count);
        return (total, tr);
    }

    /// <summary>특정 테이블의 (총 키, 번역된 키).</summary>
    public static (int total, int translated) TableCoverage(SupportedMod mod, string lang, string table)
    {
        int total = mod.EngByTable.TryGetValue(table, out var e) ? e.Count : 0;
        var ov = ReadJson(OverridePath(mod.Id, lang, table));
        int tr = ov.Count(kv => !string.IsNullOrEmpty(kv.Value));
        return (total, tr);
    }

    /// <summary>override 파일의 raw 텍스트(편집기 표시용). 없으면 "{}".</summary>
    public static string OverrideText(string modId, string lang, string table)
    {
        try
        {
            string p = OverridePath(modId, lang, table);
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : "{}";
        }
        catch { return "{}"; }
    }

    /// <summary>원본(기본 eng) 로컬라이제이션 텍스트(편집기 참조용 read-only). 없으면 "{}".</summary>
    public static string SourceText(string modId, string table, string lang = "eng")
    {
        try
        {
            string p = Path.Combine(SourceDir(modId, lang), table + ".json");
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : "{}";
        }
        catch { return "{}"; }
    }

    /// <summary>편집기 텍스트를 검증(JSON object) 후 override 로 저장. (성공여부, 오류메시지).</summary>
    public static (bool ok, string error) SaveOverrideText(string modId, string lang, string table, string text)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (d == null) return (false, "JSON 최상위가 객체가 아닙니다");
        }
        catch (Exception ex) { return (false, "JSON 파싱 오류: " + ex.Message); }
        try { WriteRaw(OverridePath(modId, lang, table), text); return (true, ""); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>외부 JSON 파일을 업로드: 템플릿 키는 유지하고 일치하는 값만 반영. (성공여부, 오류).</summary>
    public static (bool ok, string error) ImportInto(string modId, string lang, string table, string externalPath)
    {
        Dictionary<string, string>? ext;
        try
        {
            ext = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(externalPath, Encoding.UTF8));
        }
        catch (Exception ex) { return (false, "업로드 파일 파싱 오류: " + ex.Message); }
        if (ext == null) return (false, "업로드 JSON 최상위가 객체가 아닙니다");

        try
        {
            var cur = ReadJson(OverridePath(modId, lang, table)); // 템플릿(키 집합)
            var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (cur.Count > 0)
                foreach (var k in cur.Keys) merged[k] = ext.TryGetValue(k, out var v) ? v : cur[k];
            else
                foreach (var kv in ext) merged[kv.Key] = kv.Value; // 템플릿 없으면 업로드 그대로
            WriteJson(OverridePath(modId, lang, table), merged);
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>주어진 모드/언어의 override 중 값이 비어 있지 않은 것만 테이블별로 반환.</summary>
    public static Dictionary<string, Dictionary<string, string>> LoadNonEmptyOverrides(
        SupportedMod mod, string lang)
    {
        var byTable = new Dictionary<string, Dictionary<string, string>>();
        foreach (var table in mod.EngByTable.Keys)
        {
            var dict = ReadJson(OverridePath(mod.Id, lang, table));
            var nonEmpty = dict.Where(kv => !string.IsNullOrEmpty(kv.Value))
                               .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (nonEmpty.Count > 0) byTable[table] = nonEmpty;
        }
        return byTable;
    }

    /// <summary>지원/미지원 목록 + 진행률 리포트를 supported_mods.json 으로 출력.</summary>
    public static void WriteReport(ScanResult scan, string lang)
    {
        var supported = new List<object>();
        foreach (var m in scan.Supported.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            int total = m.TotalKeys;
            int translated = LoadNonEmptyOverrides(m, lang).Values.Sum(d => d.Count);
            supported.Add(new
            {
                id = m.Id,
                name = m.Name,
                version = m.Version,
                ships_langs = m.ShipsLangs.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                tables = m.EngByTable.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                keys_total = total,
                keys_translated = translated,
                coverage = total == 0 ? "n/a" : $"{(int)Math.Round(100.0 * translated / total)}%",
                edit_folder = OverrideDir(m.Id, lang),
            });
        }

        var report = new
        {
            schema = "sts2_mod_translator_report_v1",
            generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            target_lang = lang,
            supported_count = scan.Supported.Count,
            unsupported_count = scan.Unsupported.Count,
            supported,
            unsupported = scan.Unsupported
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .Select(m => new { id = m.Id, name = m.Name, reason = m.Reason })
                .ToArray(),
        };

        WriteRaw(Path.Combine(Root, "supported_mods.json"),
            JsonSerializer.Serialize(report, WriteOpts));
    }

    // ── JSON IO ─────────────────────────────────────────────────

    private static SortedDictionary<string, string> Sorted(Dictionary<string, string> d)
    {
        var s = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in d) s[kv.Key] = kv.Value;
        return s;
    }

    private static Dictionary<string, string> ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void WriteJson(string path, object dict) =>
        WriteRaw(path, JsonSerializer.Serialize(dict, WriteOpts));

    private static void WriteRaw(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
