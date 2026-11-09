using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Accessibility;

namespace WorldTime.Shell;

/// <summary>
/// Reads the calendar part of the flyout through UI Automation (the API screen
/// readers use). One targeted, cached query per read; must be used from an MTA
/// thread that is not the UI thread.
/// </summary>
internal sealed unsafe class UiaCalendarReader : IDisposable
{
    private static readonly Guid ClsidCUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid IidIUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    private IUIAutomation* _uia;
    private IUIAutomationCacheRequest* _cache;
    private IUIAutomationCondition* _keyCondition;
    private IUIAutomationTreeWalker* _walker;

    public UiaCalendarReader()
    {
        Guid clsid = ClsidCUIAutomation, iid = IidIUIAutomation;
        void* ppv;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &ppv).ThrowOnFailure();
        _uia = (IUIAutomation*)ppv;

        _cache = _uia->CreateCacheRequest();
        _cache->AddProperty(UIA_PROPERTY_ID.UIA_AutomationIdPropertyId);
        _cache->AddProperty(UIA_PROPERTY_ID.UIA_ClassNamePropertyId);
        _cache->AddProperty(UIA_PROPERTY_ID.UIA_ControlTypePropertyId);
        _cache->AddProperty(UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId);
        _cache->AddProperty(UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId);
        _cache->AddProperty(UIA_PROPERTY_ID.UIA_NamePropertyId);
        _cache->AddProperty(UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId);

        // CalendarCenterGrid is the calendar card itself (Windows 11 22H2 and later);
        // the button and month grid identify the calendar and its expanded state.
        var c1 = StringCondition(UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, "ExpandCollapseButton");
        var c2 = IntCondition(UIA_PROPERTY_ID.UIA_ControlTypePropertyId, UiNode.CalendarControlType);
        var c3 = StringCondition(UIA_PROPERTY_ID.UIA_ClassNamePropertyId, "CalendarView");
        var c4 = StringCondition(UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, UiNode.CardId);
        var conditions = stackalloc IUIAutomationCondition*[4] { c1, c2, c3, c4 };
        _keyCondition = _uia->CreateOrConditionFromNativeArray(conditions, 4);
        c1->Release(); c2->Release(); c3->Release(); c4->Release();

        _walker = _uia->RawViewWalker;
    }

    private IUIAutomationCondition* StringCondition(UIA_PROPERTY_ID prop, string value)
    {
        VARIANT v = default;
        v.Anonymous.Anonymous.vt = VARENUM.VT_BSTR;
        fixed (char* p = value) v.Anonymous.Anonymous.Anonymous.bstrVal = PInvoke.SysAllocString(p);
        try { return _uia->CreatePropertyCondition(prop, v); }
        finally { PInvoke.VariantClear(&v); }
    }

    private IUIAutomationCondition* IntCondition(UIA_PROPERTY_ID prop, int value)
    {
        VARIANT v = default;
        v.Anonymous.Anonymous.vt = VARENUM.VT_I4;
        v.Anonymous.Anonymous.Anonymous.lVal = value;
        return _uia->CreatePropertyCondition(prop, v);
    }

    /// <summary>Key calendar elements plus the expand button's ancestors (nearest first).</summary>
    public (List<UiNode> Keys, List<UiNode> ButtonAncestors)? Read(nint hwnd, bool withAncestors = true)
    {
        IUIAutomationElement* root = null;
        IUIAutomationElementArray* found = null;
        try
        {
            root = _uia->ElementFromHandle((HWND)hwnd);
            found = root->FindAllBuildCache(TreeScope.TreeScope_Descendants, _keyCondition, _cache);
            var keys = new List<UiNode>();
            var ancestors = new List<UiNode>();
            int n = found is null ? 0 : found->Length;
            for (int i = 0; i < n; i++)
            {
                var el = found->GetElement(i);
                try
                {
                    var node = ReadCached(el);
                    keys.Add(node);
                    if (withAncestors && node.IsExpandButton && ancestors.Count == 0)
                        ReadAncestors(el, ancestors);
                }
                finally
                {
                    el->Release();
                }
            }
            return (keys, ancestors);
        }
        catch (Exception e) when (e is COMException or ArgumentException or UnauthorizedAccessException
                                       or InvalidOperationException or TimeoutException)
        {
            return null; // flyout went away mid-query, or UIA is busy
        }
        finally
        {
            if (found is not null) found->Release();
            if (root is not null) root->Release();
        }
    }

    private void ReadAncestors(IUIAutomationElement* el, List<UiNode> into)
    {
        IUIAutomationElement* current = el;
        current->AddRef();
        try
        {
            for (int depth = 0; depth < 8; depth++)
            {
                var parent = _walker->GetParentElementBuildCache(current, _cache);
                current->Release();
                current = parent;
                if (current is null) return;
                var node = ReadCached(current);
                if (node.ControlType == 50032 /* Window */) return; // reached the flyout window itself
                into.Add(node);
            }
        }
        finally
        {
            if (current is not null) current->Release();
        }
    }

    private static UiNode ReadCached(IUIAutomationElement* el)
    {
        static string Take(BSTR b)
        {
            if (b.Value is null) return "";
            var s = new string(b.Value);
            PInvoke.SysFreeString(b);
            return s;
        }

        var aid = Take(el->CachedAutomationId);
        var cls = Take(el->CachedClassName);
        var name = Take(el->CachedName);
        int ctype = (int)el->CachedControlType;
        var r = el->CachedBoundingRectangle;
        bool off = el->CachedIsOffscreen;
        int expand = -1;
        var v = el->GetCachedPropertyValue(UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId);
        if (v.Anonymous.Anonymous.vt == VARENUM.VT_I4) expand = v.Anonymous.Anonymous.Anonymous.lVal;
        PInvoke.VariantClear(&v);
        return new UiNode(aid, cls, ctype, new PixelRect(r.left, r.top, r.right, r.bottom), off, expand,
            name.Length > 60 ? name[..60] : name);
    }

    public void Dispose()
    {
        if (_walker is not null) { _walker->Release(); _walker = null; }
        if (_keyCondition is not null) { _keyCondition->Release(); _keyCondition = null; }
        if (_cache is not null) { _cache->Release(); _cache = null; }
        if (_uia is not null) { _uia->Release(); _uia = null; }
    }
}
