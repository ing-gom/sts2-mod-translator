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
/// 레이아웃(데이터 파일 확장자 = <see cref="DataExt"/>):
///   source/{id}/eng/{table}.txt        — 원문(영어) 참조본. 매번 새로 씀(읽기 전용 취급).
///   overrides/{id}/{lang}/{table}.txt   — 번역 입력칸. 값이 비어 있으면 미번역(원문 fallback).
///                                         기존 파일은 절대 덮어쓰지 않고, 새 키만 추가한다.
///   supported_mods.txt                  — 지원/미지원 모드 목록 + 진행률 리포트.
/// 파일 내용은 모두 JSON 이지만, 확장자를 .json 으로 두면 STS2 ModManager 가 mods\ 트리의
/// 모든 .json 을 매니페스트로 파싱 시도해 부팅 시 "missing id" 로그를 남기므로 .txt 로 저장한다.
/// </summary>
public static class TranslationStore
{
    private static string? _rootCache;

    /// <summary>
    /// 번역 데이터 파일 확장자. 내용은 JSON 이지만 .json 으로 두면 ModManager 가 매니페스트로
    /// 오인해 부팅 로그에 "missing id" 가 쌓이므로 .txt 를 쓴다. (진짜 매니페스트만 .json 유지.)
    /// </summary>
    public const string DataExt = ".txt";

    /// <summary>
    /// 번역 데이터 루트. 1순위 = 모드 폴더 내부의 Translations\ (DLL 옆),
    /// 쓰기 불가(예: Program Files 권한) 시 2순위 = %APPDATA%\Sts2ModTranslator\.
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
            MigrateLegacyExtension(chosen); // 기존 .json 작업 파일 → .txt 1회 변환
            return chosen;
        }
    }

    /// <summary>
    /// 이전 버전이 .json 으로 저장한 작업 파일(overrides/source/리포트)을 .txt 로 1회 변환.
    /// exported\ 하위(배포용 매니페스트 등)는 건드리지 않는다. best-effort — 실패해도 무시.
    /// </summary>
    private static void MigrateLegacyExtension(string root)
    {
        foreach (var sub in new[] { "overrides", "source" })
        {
            string dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            try
            {
                // 열거 중 rename 으로 인한 문제 방지 — 목록을 먼저 확정.
                foreach (var json in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                    RenameToDataExt(json);
            }
            catch { /* 열거 실패 무시 */ }
        }
        RenameToDataExt(Path.Combine(root, "supported_mods.json"));
    }

    private static void RenameToDataExt(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath)) return;
            string dst = jsonPath.Substring(0, jsonPath.Length - ".json".Length) + DataExt;
            if (File.Exists(dst)) File.Delete(jsonPath); // 이미 변환됨 → 중복 .json 제거
            else File.Move(jsonPath, dst);
        }
        catch { /* 개별 파일 실패 무시 */ }
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
        Path.Combine(OverrideDir(id, lang), table + DataExt);

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
                WriteJson(Path.Combine(srcDir, table + DataExt), Sorted(dict));
        }

        // 2) 번역 입력칸: eng 키 기준, 기존 번역 보존 + 신규 키만 빈 값으로 추가.
        foreach (var (table, engDict) in mod.EngByTable)
        {
            string ovrPath = OverridePath(mod.Id, lang, table);
            // JSON 이 깨진 파일은 절대 덮어쓰지 않는다(사용자 번역 유실 방지).
            // UI 가 '오류'로 표시하고, 편집기에서 고쳐 저장하면 정상화된다.
            if (File.Exists(ovrPath) && !TryReadJson(ovrPath, out _, out _)) continue;
            var existing = ReadJson(ovrPath); // 없음/빈/정상 → dict
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

    /// <summary>
    /// 한 테이블에 주입할 최종 dict 를 만든다. 키마다 우선순위:
    ///   1) 로컬 override 값(비어 있지 않으면) — 사용자가 인게임 에디터로 직접 한 번역
    ///   2) 설치된 번역 모드의 값(bundled, 비어 있지 않으면)
    ///   3) 모드가 현재 언어를 직접 동봉했으면 그 원본값
    ///   4) eng 기본값
    /// 모든 키를 명시적으로 설정하므로 "로컬 번역을 비우면 (번역 모드 → 원문 순으로) 복귀" 가 보장된다.
    /// bundled 는 설치된 번역 모드가 이 (대상모드, 언어, 테이블)에 제공한 (키→값). 없으면 null/빈 dict.
    /// </summary>
    public static Dictionary<string, string> BuildInjectTable(
        SupportedMod mod, string lang, string table, IReadOnlyDictionary<string, string>? bundled = null)
    {
        var eng = mod.EngByTable.TryGetValue(table, out var e) ? e : new Dictionary<string, string>();
        Dictionary<string, string>? shipped = null;
        if (mod.ByLang.TryGetValue(lang, out var byTable) && byTable.TryGetValue(table, out var st))
            shipped = st; // 모드가 현재 언어를 직접 동봉한 경우의 원본값

        var ov = ReadJson(OverridePath(mod.Id, lang, table));

        // eng 키 ∪ bundled 키 — 번역 모드가 eng 에 없는 키를 줘도 누락 없이 주입.
        var keys = new HashSet<string>(eng.Keys, StringComparer.Ordinal);
        if (bundled != null) foreach (var k in bundled.Keys) keys.Add(k);

        var result = new Dictionary<string, string>(keys.Count);
        foreach (var key in keys)
        {
            if (ov.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                result[key] = v;                                   // 1) 로컬 번역값
            else if (bundled != null && bundled.TryGetValue(key, out var bv) && !string.IsNullOrEmpty(bv))
                result[key] = bv;                                  // 2) 설치된 번역 모드
            else if (shipped != null && shipped.TryGetValue(key, out var sv) && !string.IsNullOrEmpty(sv))
                result[key] = sv;                                  // 3) 동봉 원본(현재 언어)
            else if (eng.TryGetValue(key, out var ev))
                result[key] = ev;                                  // 4) eng 기본값
        }
        return result;
    }

    /// <summary>
    /// UI 파일 목록용 상태: (총 키, 번역된 키, JSON 깨짐 여부).
    /// invalid=true 면 파일을 파싱할 수 없어 번역이 적용되지 않는 상태(편집기에서 수정 필요).
    /// 번역 카운트는 템플릿(eng) 키 집합 안에서만 센다.
    /// </summary>
    public static (int total, int translated, bool invalid) TableStatus(
        SupportedMod mod, string lang, string table)
    {
        var keys = mod.EngByTable.TryGetValue(table, out var e) ? e : new Dictionary<string, string>();
        int total = keys.Count;
        if (!TryReadJson(OverridePath(mod.Id, lang, table), out var ov, out _))
            return (total, 0, true); // JSON 형식 오류 — 적용 안 됨
        int tr = ov.Count(kv => keys.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value));
        return (total, tr, false);
    }

    /// <summary>override 파일의 raw 텍스트(편집기 표시용). 없으면 "{}".</summary>
    public static string OverrideText(string modId, string lang, string table)
    {
        try
        {
            string p = ReadPath(OverridePath(modId, lang, table));
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : "{}";
        }
        catch { return "{}"; }
    }

    /// <summary>원본(기본 eng) 로컬라이제이션 텍스트(편집기 참조용 read-only). 없으면 "{}".</summary>
    public static string SourceText(string modId, string table, string lang = "eng")
    {
        try
        {
            string p = ReadPath(Path.Combine(SourceDir(modId, lang), table + DataExt));
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

    /// <summary>한 테이블의 override 를 소스 키 + 빈 값으로 덮어쓴다(= 전부 원문 복귀).</summary>
    public static void ResetOverride(SupportedMod mod, string lang, string table)
    {
        var eng = mod.EngByTable.TryGetValue(table, out var e) ? e : new Dictionary<string, string>();
        var empty = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in eng.Keys) empty[k] = "";
        WriteJson(OverridePath(mod.Id, lang, table), empty);
    }

    /// <summary>한 모드/언어의 모든 테이블 override 를 초기화.</summary>
    public static void ResetLanguage(SupportedMod mod, string lang)
    {
        foreach (var table in mod.EngByTable.Keys) ResetOverride(mod, lang, table);
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

    // ── 번역 모드 내보내기 ──────────────────────────────────────

    /// <summary>
    /// 내보낸 번역 모드들이 모이는 폴더. 항상 %APPDATA%\Sts2ModTranslator\exported 를 쓴다
    /// (Root 가 모드 폴더 내부일 수 있는데, 그 안에 또 다른 모드 매니페스트를 두면 게임이
    /// 중첩 모드로 오인할 수 있으므로 의도적으로 모드 폴더 밖으로 내보낸다).
    /// </summary>
    public static string ExportRoot
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Sts2ModTranslator", "exported");
        }
    }

    /// <summary>
    /// 한 대상 모드의 (비어 있지 않은) 번역을 배포 가능한 독립 "번역 모드" 폴더로 내보낸다.
    /// 결과 레이아웃:
    ///   exported/{modId}_Translation/
    ///     {modId}_Translation.json                      — 매니페스트(dependencies: [Sts2ModTranslator])
    ///     translations/{대상id}/{lang}/{table}.txt      — 번역값(JSON 내용, 비어 있지 않은 키만)
    /// 사용자는 이 폴더를 STS2 mods\ 에 넣거나 Workshop 에 올려 배포할 수 있다.
    /// 반환: (성공여부, 생성된 폴더 경로, 오류). 내보낼 번역이 하나도 없으면 실패.
    /// </summary>
    public static (bool ok, string path, string error) ExportMod(SupportedMod mod, string author = "")
    {
        try
        {
            // 1) 비어 있지 않은 번역을 가진 언어/테이블만 수집.
            var langs = new List<(string lang, Dictionary<string, Dictionary<string, string>> tables)>();
            foreach (var lang in AllOverrideLangs(mod.Id))
            {
                var tables = LoadNonEmptyOverrides(mod, lang);
                if (tables.Count > 0) langs.Add((lang, tables));
            }
            if (langs.Count == 0)
                return (false, "", "내보낼 번역이 없습니다 — 먼저 한 항목 이상 번역하세요.");

            string newId = Sanitize(mod.Id) + "_Translation";
            string modDir = Path.Combine(ExportRoot, newId);
            // 기존 내보내기는 새로 덮어쓴다(매번 최신 번역 반영). translations 하위만 청소.
            string trDir = Path.Combine(modDir, BundledTranslationScanner.FolderName, mod.Id);
            if (Directory.Exists(trDir)) Directory.Delete(trDir, recursive: true);

            var langCodes = new List<string>();
            foreach (var (lang, tables) in langs)
            {
                langCodes.Add(lang);
                foreach (var (table, dict) in tables)
                {
                    var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    foreach (var kv in dict) sorted[kv.Key] = kv.Value;
                    // 번역 데이터는 .txt 로 — 설치 시 ModManager 의 'missing id' 로그 회피.
                    WriteJson(Path.Combine(trDir, lang, table + DataExt), sorted);
                }
            }

            // 2) 매니페스트.
            var manifest = new
            {
                id = newId,
                name = mod.Name + " Translation",
                author,
                description =
                    $"Translation pack for \"{mod.Name}\" ({string.Join(", ", langCodes)}). "
                    + "Requires the STS2 Mod Translator mod to apply.",
                version = "1.0.0",
                has_pck = false,
                has_dll = false,
                dependencies = new[] { MainFile.ModId },
                affects_gameplay = false,
            };
            WriteRaw(Path.Combine(modDir, newId + ".json"),
                JsonSerializer.Serialize(manifest, WriteOpts));

            return (true, modDir, "");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    /// <summary>해당 대상 모드의 overrides\{id}\ 아래 존재하는 언어 폴더 목록.</summary>
    private static List<string> AllOverrideLangs(string id)
    {
        string dir = Path.Combine(Root, "overrides", id);
        if (!Directory.Exists(dir)) return new();
        try
        {
            return Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>모드 id 로 쓸 수 있도록 파일시스템 안전 문자만 남긴다.</summary>
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        string r = sb.ToString();
        return string.IsNullOrEmpty(r) ? "Mod" : r;
    }

    /// <summary>지원/미지원 목록 + 진행률 리포트를 supported_mods.txt(JSON 내용)로 출력.</summary>
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
            // 설치된 번역 모드(Sts2ModTranslator 를 참조해 번역을 동봉한 모드)들.
            translation_packs = scan.Bundled.Providers
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new { id = p.Id, name = p.Name, targets = p.Targets.ToArray(), keys = p.Keys })
                .ToArray(),
        };

        WriteRaw(Path.Combine(Root, "supported_mods" + DataExt),
            JsonSerializer.Serialize(report, WriteOpts));
    }

    // ── JSON IO ─────────────────────────────────────────────────

    private static SortedDictionary<string, string> Sorted(Dictionary<string, string> d)
    {
        var s = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in d) s[kv.Key] = kv.Value;
        return s;
    }

    /// <summary>
    /// JSON 파일을 {string:string} 으로 읽기 시도.
    /// 반환 true = 정상(파일 없음/빈 파일 포함), false = JSON 형식 오류.
    /// dict 는 성공 시 내용(없음/빈 파일이면 빈 dict), error 에 실패 사유.
    /// </summary>
    private static bool TryReadJson(string path, out Dictionary<string, string> dict, out string error)
    {
        dict = new();
        error = "";
        try
        {
            path = ReadPath(path);
            if (!File.Exists(path)) return true;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return true;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (d == null) { error = "최상위가 객체가 아닙니다"; return false; }
            dict = d;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Dictionary<string, string> ReadJson(string path)
        => TryReadJson(path, out var d, out _) ? d : new();

    /// <summary>
    /// 읽기용 경로 보정: .txt 가 없고 레거시 .json 만 있으면 .json 경로를 돌려준다.
    /// (확장자 마이그레이션이 어떤 이유로 누락된 파일도 안전하게 읽기 위함.)
    /// </summary>
    private static string ReadPath(string path)
    {
        try
        {
            if (File.Exists(path) || !path.EndsWith(DataExt, StringComparison.Ordinal)) return path;
            string legacy = path.Substring(0, path.Length - DataExt.Length) + ".json";
            return File.Exists(legacy) ? legacy : path;
        }
        catch { return path; }
    }

    private static void WriteJson(string path, object dict) =>
        WriteRaw(path, JsonSerializer.Serialize(dict, WriteOpts));

    private static void WriteRaw(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
