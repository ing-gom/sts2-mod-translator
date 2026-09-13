using System;

namespace Sts2ModTranslator.Core;

/// <summary>자동 번역에 쓸 공급자.</summary>
public enum AutoProvider
{
    /// <summary>DeepL /v2/translate. 순수 번역 품질이 좋아 기본값.</summary>
    DeepL = 0,

    /// <summary>
    /// OpenAI Chat Completions 형식을 쓰는 임의의 엔드포인트. 이 형식이 사실상 표준이라
    /// 상용(OpenAI·OpenRouter·Together…)부터 로컬(Ollama·LM Studio)까지 한 코드로 붙는다.
    /// ★DeepL 이 서비스하지 않는 지역의 탈출구이기도 하다 — 로컬 모델은 지역 차단 자체가 무의미.
    /// </summary>
    OpenAiCompatible = 1,
}

/// <summary>
/// 자동 번역 설정. DeepL 키는 기존 파일(deepl_key)에 그대로 두고, 공급자 선택과
/// OpenAI 호환 엔드포인트 설정만 별도 파일에 담는다 — 기존 사용자의 키가 마이그레이션
/// 없이 계속 쓰이도록.
/// </summary>
public sealed class AutoConfig
{
    public AutoProvider Provider { get; set; } = AutoProvider.DeepL;

    /// <summary>DeepL API 키. (저장은 기존 deepl_key 파일.)</summary>
    public string DeepLKey { get; set; } = "";

    /// <summary>OpenAI 호환 엔드포인트의 베이스 주소. 예: https://api.openai.com/v1</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>모델 이름. 예: gpt-4o-mini, qwen2.5:7b</summary>
    public string Model { get; set; } = "";

    /// <summary>OpenAI 호환 엔드포인트의 키. 로컬 모델은 보통 비워도 된다.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>선택된 공급자를 쓸 준비가 됐는지(필수 항목이 다 찼는지).</summary>
    public bool IsReady => Provider == AutoProvider.DeepL
        ? DeepLKey.Trim().Length > 0
        : BaseUrl.Trim().Length > 0 && Model.Trim().Length > 0;

    /// <summary>UI 표시용 공급자 이름.</summary>
    public string ProviderName =>
        Provider == AutoProvider.DeepL ? "DeepL" : "AI endpoint";

    /// <summary>
    /// 사용자가 입력한 베이스 주소를 실제 요청 URL 로 정규화한다.
    /// 받아들이는 형태(흔한 오입력을 전부 흡수):
    ///   https://api.openai.com/v1                 → …/v1/chat/completions
    ///   https://api.openai.com/v1/                → …/v1/chat/completions
    ///   https://api.openai.com/v1/chat/completions→ 그대로
    ///   http://localhost:11434                    → …/v1/chat/completions  (경로가 없으면 /v1 을 붙인다)
    /// </summary>
    public static string ChatUrl(string baseUrl)
    {
        string s = (baseUrl ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return "";
        if (s.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return s;

        // 경로가 비어 있으면(호스트만 입력) /v1 을 보충한다 — Ollama 등에서 흔한 입력.
        try
        {
            var u = new Uri(s, UriKind.Absolute);
            if (u.AbsolutePath == "/" || u.AbsolutePath.Length == 0) s += "/v1";
        }
        catch { /* 상대주소 등 — 그대로 두고 아래에서 붙인다 */ }

        return s + "/chat/completions";
    }
}
