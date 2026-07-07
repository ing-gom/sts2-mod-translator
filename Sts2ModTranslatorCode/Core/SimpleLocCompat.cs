using System;
using System.Collections.Generic;
using System.Reflection;

namespace Sts2ModTranslator.Core;

/// <summary>
/// BaseLib "SimpleLoc" 저작 문법 호환 계층.
///
/// BaseLib(여러 커스텀 카드·캐릭터 모드가 공유하는 라이브러리, 모드 id "BaseLib")는 모드가 로크
/// 텍스트를 간이 문법으로 쓸 수 있게 해준다. 맨 앞 '#' 로 opt-in 하면, 로드 시점에 BaseLib 이
/// *파일 로더 postfix*(<c>SimpleLoc.ProcessSimpleLoc</c>)에서 STS2 네이티브 SmartFormat 으로 변환한다:
///   <list type="bullet">
///     <item><c>!Var!</c>  → <c>{Var:diff()}</c>  (Damage/Block/Cards/Energy/Heal 은 축약 — 예: <c>!D!</c> → <c>{Damage:diff()}</c>)</item>
///     <item><c>@Var@</c>  → <c>{Var:inverseDiff()}</c></item>
///     <item><c>*txt*</c>  → <c>[gold]txt[/gold]</c>,  <c>$txt$</c> → <c>[blue]txt[/blue]</c></item>
///     <item><c>[E]/[EEE]</c> → 에너지 아이콘,  <c>-a-</c>/<c>+b+</c> → 강화 스왑,  <c>/</c> = 이스케이프</item>
///   </list>
///
/// 문제: 이 변환은 BaseLib 이 *파일을 로드할 때만* 돈다. 우리는 번역을 런타임에
/// <c>LocTable.MergeWith</c> 로 주입하므로 그 로더 postfix 를 타지 않는다. 따라서 주입한 번역엔
/// '<c>#!Var!</c>' 원형이 남고, STS2 SmartFormat 은 '<c>{}</c>' 만 해석하므로 게임에 '<c>!Var!</c>' 가
/// 그대로 노출된다(영문은 로드 시 이미 변환돼 정상 표시됨).
///
/// 해결: 주입 직전 각 값을 BaseLib 자신의 <c>SimpleLoc.TrySimplify</c> 로 통과시켜 영문과 동일하게
/// 변환한다. BaseLib 의 실제 메서드를 리플렉션으로 호출하므로 문법이 바뀌어도 드리프트가 없다.
/// '#' 로 시작하지 않는 값은 <c>TrySimplify</c> 가 그대로 돌려주므로, BaseLib 을 쓰지 않는 모드
/// (STS2 '<c>{}</c>' 문법을 이미 쓰는 cthulhumiko 등)에는 무해하다. BaseLib 이 없으면 no-op.
/// </summary>
internal static class SimpleLocCompat
{
    private const string TypeName = "BaseLib.Patches.Localization.SimpleLoc";

    // BaseLib 은 우리보다 늦게 로드될 수 있어 lazy 해석. 찾으면 캐시(이후 재스캔 없음),
    // 못 찾으면 다음 호출에서 재시도(BaseLib 미설치면 계속 no-op).
    private static MethodInfo? _trySimplify;

    /// <summary>주입할 (키→값) dict 를 BaseLib SimpleLoc 문법에서 STS2 네이티브 문법으로 변환한 새 dict.</summary>
    public static Dictionary<string, string> ApplyAll(IReadOnlyDictionary<string, string> src)
    {
        var result = new Dictionary<string, string>(src.Count);
        foreach (var kv in src) result[kv.Key] = Apply(kv.Value);
        return result;
    }

    /// <summary>한 값을 BaseLib 의 <c>TrySimplify</c> 로 변환. BaseLib 이 없거나 실패하면 원본 그대로.</summary>
    public static string Apply(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var m = Resolve();
        if (m == null) return value;
        try
        {
            return m.Invoke(null, new object[] { value }) as string ?? value;
        }
        catch
        {
            return value; // BaseLib 내부 예외 → 원본 유지(주입은 계속 진행)
        }
    }

    private static MethodInfo? Resolve()
    {
        if (_trySimplify != null) return _trySimplify;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t;
                try { t = asm.GetType(TypeName, throwOnError: false); }
                catch { continue; } // 일부 동적 어셈블리는 GetType 이 throw — 스킵
                if (t == null) continue;
                var m = t.GetMethod("TrySimplify", BindingFlags.Public | BindingFlags.Static,
                                    binder: null, types: new[] { typeof(string) }, modifiers: null);
                if (m != null && m.ReturnType == typeof(string))
                {
                    _trySimplify = m; // 이후 이른 반환으로 재스캔·재로그 없음
                    MainFile.Logger.Info(
                        "[Sts2ModTranslator] BaseLib SimpleLoc 감지 — 주입 값에 저작 문법 변환 적용.");
                    return m;
                }
            }
        }
        catch { /* 어셈블리 열거 실패 — 다음 호출에서 재시도 */ }
        return null;
    }
}
