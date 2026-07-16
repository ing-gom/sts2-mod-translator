using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sts2ModTranslator.Core;

/// <summary>
/// Translations/ 폴더를 AI 에이전트(Claude Code 등)가 바로 작업할 수 있는 워크스페이스로 만든다.
///   .claude/skills/translate-mod/SKILL.md  — 번역 규칙(임베디드 ai_skill.md 를 그대로 풀어 씀)
///   glossary_{lang}.txt                    — 그 언어의 게임 공식 용어표
///
/// ★설계: 스킬 본문은 <b>정적</b>으로 두고 상태(용어표·모드목록·진행률)는 별도 파일로 둔다.
/// 스킬이 디스크에서 상태를 읽으므로 상태가 변해도 스킬을 다시 만들 필요가 없다.
///
/// ★경로를 주입하지 않는 이유: .claude/skills 는 cwd 기준으로 발견되므로, 파일이 이 폴더 안에
/// 있으면 에이전트에게 넘길 경로 자체가 없다(모든 경로가 상대경로). 대신 워크샵 업데이트가
/// mods/ 폴더를 갈아엎으면 같이 날아가므로, 리포트와 동일한 주기로 매번 다시 쓴다(자가 치유).
/// </summary>
public static class AiKitWriter
{
    private const string SkillResource = "ai_skill.md";
    private const string SkillRelPath = ".claude/skills/translate-mod/SKILL.md";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 비ASCII 를 그대로 — 사람이 읽을 수 있게
    };

    /// <summary>
    /// 현재 언어 기준으로 AI 킷을 갱신. 리포트(supported_mods.txt)와 같은 지점에서 호출된다 —
    /// 스킬이 그 리포트를 작업목록으로 참조하므로 둘의 라이프사이클은 같아야 한다.
    /// 실패해도 번역 기능 자체에는 영향이 없으므로 조용히 로그만 남긴다.
    /// </summary>
    public static void Write(string lang)
    {
        try
        {
            string root = TranslationStore.Root;
            WriteIfChanged(Path.Combine(root, SkillRelPath.Replace('/', Path.DirectorySeparatorChar)),
                           LoadSkillText());
            WriteGlossary(root, lang);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] AI 킷 생성 실패(번역 기능에는 영향 없음): {ex.Message}");
        }
    }

    /// <summary>해당 언어의 공식 용어표를 {"Vulnerable":"취약", ...} 로 덤프. 용어표가 없으면 no-op.</summary>
    private static void WriteGlossary(string root, string lang)
    {
        var g = AutoTranslator.GlossaryFor(lang);
        if (g == null) return; // 영어 등 용어표가 없는 언어 — 파일을 만들지 않는다.
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in g) sorted[kv.Key] = kv.Value;
        WriteIfChanged(Path.Combine(root, $"glossary_{lang}{TranslationStore.DataExt}"),
                       JsonSerializer.Serialize(sorted, WriteOpts));
    }

    /// <summary>임베디드 ai_skill.md 본문. 리소스가 없으면 예외 → 호출부에서 경고 후 무시.</summary>
    private static string LoadSkillText()
    {
        var asm = typeof(AiKitWriter).Assembly;
        string name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(SkillResource, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"embedded resource '{SkillResource}' 없음");
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' 스트림 열기 실패");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>
    /// 내용이 같으면 건너뛴다 — 부팅마다 mtime 을 흔들면 열려 있는 에디터가 불필요하게 리로드된다.
    /// </summary>
    private static void WriteIfChanged(string path, string content)
    {
        try { if (File.Exists(path) && File.ReadAllText(path) == content) return; }
        catch { /* 읽기 실패 → 그냥 덮어쓴다 */ }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
