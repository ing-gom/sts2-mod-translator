using System;
using Godot;

namespace Sts2ModTranslator.Ui;

/// <summary>
/// 번역 패널의 루트 Control. 하는 일은 하나 — <b>패널이 보이는 동안의 ESC 를 가로채</b>
/// 패널만 닫는다.
/// <para>
/// 가로채지 않으면 ESC 가 게임까지 가서 일시정지 메뉴가 통째로 닫히고, 패널은 그 자식이라
/// 같이 숨어 런으로 돌아가 버린다(= 편집하다 실수로 ESC 를 누르면 전투 한복판). 여기서 닫으면
/// 일시정지 메뉴에 그대로 남는다.
/// </para>
/// ★<c>_Input</c> 은 GUI·<c>_UnhandledInput</c> 보다 먼저 돌기 때문에, 여기서
/// <c>SetInputAsHandled()</c> 하면 게임의 일시정지 처리보다 확실히 앞선다.
/// </summary>
public partial class TranslatorPanelRoot : Control
{
    /// <summary>ESC 로 닫을 때 실행할 동작(패널 Hide). 패널을 만드는 쪽에서 채운다.</summary>
    public Action? OnCancel;

    public override void _Input(InputEvent @event)
    {
        if (!IsVisibleInTree()) return; // 숨어 있을 땐 게임의 ESC 를 건드리지 않는다
        if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;
        if (key.Keycode != Key.Escape) return;

        GetViewport().SetInputAsHandled();
        OnCancel?.Invoke();
    }
}
