using System;
using System.Runtime.InteropServices;

namespace AIAnnounce;

// Minimal native UI Automation interop. The managed System.Windows.Automation client can't see
// inside Windows Terminal's XAML island; the COM API can. Unused vtable slots are placeholders,
// declared only so the methods we call land on the right slot.
static class Uia
{
    public const int TreeScopeDescendants = 4;
    public const int ControlTypePropertyId = 30003;
    public const int TabItemControlTypeId = 50019;
    public const int SelectionItemPatternId = 10010;

    public static IUIAutomation Create() => (IUIAutomation)new CUIAutomation();
}

[ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
class CUIAutomation { }

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomation
{
    void CompareElements();
    void CompareRuntimeIds();
    void GetRootElement();
    IUIAutomationElement ElementFromHandle(IntPtr hwnd);
    void ElementFromPoint();
    void GetFocusedElement();
    void GetRootElementBuildCache();
    void ElementFromHandleBuildCache();
    void ElementFromPointBuildCache();
    void GetFocusedElementBuildCache();
    void CreateTreeWalker();
    void GetControlViewWalker();
    void GetContentViewWalker();
    void GetRawViewWalker();
    void GetRawViewCondition();
    void GetControlViewCondition();
    void GetContentViewCondition();
    void CreateCacheRequest();
    IUIAutomationCondition CreateTrueCondition();
    void CreateFalseCondition();
    IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId();
    void FindFirst();
    IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);
    void FindFirstBuildCache();
    void FindAllBuildCache();
    void BuildUpdatedCache();
    void GetCurrentPropertyValue();
    void GetCurrentPropertyValueEx();
    void GetCachedPropertyValue();
    void GetCachedPropertyValueEx();
    void GetCurrentPatternAs();
    void GetCachedPatternAs();
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object GetCurrentPattern(int patternId);
    void GetCachedPattern();
    void GetCachedParent();
    void GetCachedChildren();
    int GetCurrentProcessId();
    int GetCurrentControlType();
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetCurrentLocalizedControlType();
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetCurrentName();
}

[ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationElementArray
{
    int GetLength();
    IUIAutomationElement GetElement(int index);
}

[ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationCondition { }

[ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationSelectionItemPattern
{
    void Select();
}
