//! World Time Flyout - flyout probe.
//!
//! Diagnostic tool, not the product. It watches the Windows 11 taskbar
//! calendar / notification center flyout and logs what it can learn through
//! documented APIs only (out-of-process WinEvents + UI Automation):
//!   * which process / window class hosts the flyout
//!   * the flyout's UIA tree, the calendar element bounds, collapse state
//!   * whether clicking a non-activating companion window keeps the flyout open
//! No injection, no hooks into other processes, no admin rights.

#![windows_subsystem = "windows"]

use std::cell::RefCell;
use std::collections::HashMap;
use std::fs::File;
use std::io::Write;
use std::path::PathBuf;
use std::sync::mpsc::{channel, Receiver, RecvTimeoutError, Sender};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use windows::core::{w, BOOL, PCWSTR, PWSTR};
use windows::Win32::Foundation::*;
use windows::Win32::Globalization::*;
use windows::Win32::Graphics::Dwm::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::Com::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Registry::*;
use windows::Win32::System::SystemInformation::GetLocalTime;
use windows::Win32::System::Threading::*;
use windows::Win32::UI::Accessibility::*;
use windows::Win32::UI::HiDpi::*;
use windows::Win32::UI::Input::KeyboardAndMouse::{ReleaseCapture, SetCapture};
use windows::Win32::UI::Shell::*;
use windows::Win32::UI::WindowsAndMessaging::*;

// ---------------------------------------------------------------- logging

struct Logger {
    file: File,
    start: Instant,
}
static LOG: OnceLock<Mutex<Logger>> = OnceLock::new();
static LOG_PATH: OnceLock<PathBuf> = OnceLock::new();

fn log(s: impl AsRef<str>) {
    if let Some(m) = LOG.get() {
        if let Ok(mut l) = m.lock() {
            let t = l.start.elapsed().as_secs_f64();
            for (i, line) in s.as_ref().lines().enumerate() {
                if i == 0 {
                    let _ = writeln!(l.file, "[{t:9.3}] {line}");
                } else {
                    let _ = writeln!(l.file, "            {line}");
                }
            }
            let _ = l.file.flush();
        }
    }
}

fn init_log() {
    let dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."));
    let st = unsafe { GetLocalTime() };
    let name = format!(
        "flyout-probe-{:04}{:02}{:02}-{:02}{:02}{:02}.log",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond
    );
    let mut path = dir.join(&name);
    let file = match File::create(&path) {
        Ok(f) => f,
        Err(_) => {
            // folder not writable: fall back to %TEMP%
            path = std::env::temp_dir().join(&name);
            File::create(&path).expect("cannot create log file")
        }
    };
    let _ = LOG_PATH.set(path);
    let _ = LOG.set(Mutex::new(Logger { file, start: Instant::now() }));
    log(format!(
        "flyout-probe 0.1.0 started {:04}-{:02}-{:02} {:02}:{:02}:{:02} local",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond
    ));
}

// ---------------------------------------------------------------- helpers

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn from_wide(buf: &[u16]) -> String {
    let n = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..n])
}

fn class_of(h: HWND) -> String {
    let mut b = [0u16; 256];
    let n = unsafe { GetClassNameW(h, &mut b) };
    String::from_utf16_lossy(&b[..n.max(0) as usize])
}

fn title_of(h: HWND) -> String {
    let mut b = [0u16; 256];
    let n = unsafe { GetWindowTextW(h, &mut b) };
    String::from_utf16_lossy(&b[..n.max(0) as usize])
}

fn rect_of(h: HWND) -> RECT {
    let mut r = RECT::default();
    unsafe {
        // visible frame bounds (no invisible resize borders)
        if DwmGetWindowAttribute(
            h,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            &mut r as *mut _ as *mut _,
            std::mem::size_of::<RECT>() as u32,
        )
        .is_err()
        {
            let _ = GetWindowRect(h, &mut r);
        }
    }
    r
}

fn cloaked(h: HWND) -> u32 {
    let mut v: u32 = 0;
    unsafe {
        let _ = DwmGetWindowAttribute(h, DWMWA_CLOAKED, &mut v as *mut _ as *mut _, 4);
    }
    v
}

fn visible(h: HWND) -> bool {
    unsafe { IsWindowVisible(h).as_bool() }
}

fn fmt_rect(r: &RECT) -> String {
    format!(
        "({},{} {}x{})",
        r.left,
        r.top,
        r.right - r.left,
        r.bottom - r.top
    )
}

fn pid_of(h: HWND) -> u32 {
    let mut pid = 0u32;
    unsafe { GetWindowThreadProcessId(h, Some(&mut pid)) };
    pid
}

thread_local! {
    static PROC_CACHE: RefCell<HashMap<u32, String>> = RefCell::new(HashMap::new());
}

fn proc_name(pid: u32) -> String {
    if let Some(n) = PROC_CACHE.with(|c| c.borrow().get(&pid).cloned()) {
        return n;
    }
    let name = unsafe {
        match OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            Ok(hp) => {
                let mut buf = [0u16; 520];
                let mut len = buf.len() as u32;
                let r = QueryFullProcessImageNameW(
                    hp,
                    PROCESS_NAME_WIN32,
                    PWSTR(buf.as_mut_ptr()),
                    &mut len,
                );
                let _ = CloseHandle(hp);
                if r.is_ok() {
                    let full = String::from_utf16_lossy(&buf[..len as usize]);
                    full.rsplit('\\').next().unwrap_or("").to_lowercase()
                } else {
                    "?".into()
                }
            }
            Err(_) => "?".into(),
        }
    };
    PROC_CACHE.with(|c| c.borrow_mut().insert(pid, name.clone()));
    name
}

fn is_shell_host(p: &str) -> bool {
    p == "shellexperiencehost.exe" || p == "shellhost.exe"
}

fn reg_dword(root: HKEY, sub: &str, val: &str) -> Option<u32> {
    let (s, v) = (wide(sub), wide(val));
    let mut data: u32 = 0;
    let mut size: u32 = 4;
    let r = unsafe {
        RegGetValueW(
            root,
            PCWSTR(s.as_ptr()),
            PCWSTR(v.as_ptr()),
            RRF_RT_REG_DWORD,
            None,
            Some(&mut data as *mut _ as *mut _),
            Some(&mut size),
        )
    };
    if r == ERROR_SUCCESS { Some(data) } else { None }
}

fn reg_str(root: HKEY, sub: &str, val: &str) -> Option<String> {
    let (s, v) = (wide(sub), wide(val));
    let mut buf = [0u16; 512];
    let mut size: u32 = (buf.len() * 2) as u32;
    let r = unsafe {
        RegGetValueW(
            root,
            PCWSTR(s.as_ptr()),
            PCWSTR(v.as_ptr()),
            RRF_RT_REG_SZ,
            None,
            Some(buf.as_mut_ptr() as *mut _),
            Some(&mut size),
        )
    };
    if r == ERROR_SUCCESS { Some(from_wide(&buf)) } else { None }
}

fn dpi_for_rect(r: &RECT) -> u32 {
    unsafe {
        let mon = MonitorFromRect(r, MONITOR_DEFAULTTONEAREST);
        let (mut x, mut y) = (96u32, 96u32);
        let _ = GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &mut x, &mut y);
        x
    }
}

// ---------------------------------------------------------------- system info

fn log_system_info() {
    let cv = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
    log(format!(
        "OS: {} | {} | build {}.{} | edition {}",
        reg_str(HKEY_LOCAL_MACHINE, cv, "ProductName").unwrap_or_default(),
        reg_str(HKEY_LOCAL_MACHINE, cv, "DisplayVersion").unwrap_or_default(),
        reg_str(HKEY_LOCAL_MACHINE, cv, "CurrentBuild").unwrap_or_default(),
        reg_dword(HKEY_LOCAL_MACHINE, cv, "UBR").unwrap_or(0),
        reg_str(HKEY_LOCAL_MACHINE, cv, "EditionID").unwrap_or_default(),
    ));
    // (ProductName still says "Windows 10" on Win11 for compat; build >= 22000 = Win11)

    let pers = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
    log(format!(
        "Theme: AppsUseLightTheme={:?} SystemUsesLightTheme={:?} EnableTransparency={:?}",
        reg_dword(HKEY_CURRENT_USER, pers, "AppsUseLightTheme"),
        reg_dword(HKEY_CURRENT_USER, pers, "SystemUsesLightTheme"),
        reg_dword(HKEY_CURRENT_USER, pers, "EnableTransparency"),
    ));
    let dwm = "Software\\Microsoft\\Windows\\DWM";
    log(format!(
        "DWM: AccentColor={:08X?} ColorPrevalence={:?}",
        reg_dword(HKEY_CURRENT_USER, dwm, "AccentColor"),
        reg_dword(HKEY_CURRENT_USER, dwm, "ColorPrevalence"),
    ));
    // WinRT UISettings accent (what the real app will use)
    match windows::UI::ViewManagement::UISettings::new() {
        Ok(ui) => {
            use windows::UI::ViewManagement::UIColorType;
            let mut parts = vec![];
            for (n, t) in [
                ("Accent", UIColorType::Accent),
                ("AccentLight1", UIColorType::AccentLight1),
                ("AccentDark1", UIColorType::AccentDark1),
                ("Background", UIColorType::Background),
                ("Foreground", UIColorType::Foreground),
            ] {
                if let Ok(c) = ui.GetColorValue(t) {
                    parts.push(format!("{n}=#{:02X}{:02X}{:02X}", c.R, c.G, c.B));
                }
            }
            let ts = ui.TextScaleFactor().unwrap_or(0.0);
            let anim = ui.AnimationsEnabled().map(|b| b.to_string()).unwrap_or_default();
            log(format!(
                "UISettings: {} TextScaleFactor={ts} AnimationsEnabled={anim}",
                parts.join(" ")
            ));
        }
        Err(e) => log(format!("UISettings unavailable: {e}")),
    }
    unsafe {
        let mut hc = HIGHCONTRASTW {
            cbSize: std::mem::size_of::<HIGHCONTRASTW>() as u32,
            ..Default::default()
        };
        let _ = SystemParametersInfoW(
            SPI_GETHIGHCONTRAST,
            hc.cbSize,
            Some(&mut hc as *mut _ as *mut _),
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
        );
        log(format!(
            "HighContrast on={}",
            (hc.dwFlags.0 & HCF_HIGHCONTRASTON.0) != 0
        ));
    }

    // locale + time format
    unsafe {
        let mut loc = [0u16; 85];
        GetUserDefaultLocaleName(&mut loc);
        let mut tf = [0u16; 80];
        let mut stf = [0u16; 80];
        GetLocaleInfoEx(PCWSTR(loc.as_ptr()), LOCALE_STIMEFORMAT, Some(&mut tf));
        GetLocaleInfoEx(PCWSTR(loc.as_ptr()), LOCALE_SSHORTTIME, Some(&mut stf));
        log(format!(
            "Locale: {} TimeFormat='{}' ShortTime='{}'",
            from_wide(&loc),
            from_wide(&tf),
            from_wide(&stf)
        ));
    }
    let tz_key = "SYSTEM\\CurrentControlSet\\Control\\TimeZoneInformation";
    log(format!(
        "TimeZone: {}",
        reg_str(HKEY_LOCAL_MACHINE, tz_key, "TimeZoneKeyName").unwrap_or_default()
    ));
    let adv = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";
    log(format!(
        "Taskbar prefs: TaskbarAl={:?} ShowSecondsInSystemClock={:?} TaskbarSi={:?} MMTaskbarEnabled={:?}",
        reg_dword(HKEY_CURRENT_USER, adv, "TaskbarAl"),
        reg_dword(HKEY_CURRENT_USER, adv, "ShowSecondsInSystemClock"),
        reg_dword(HKEY_CURRENT_USER, adv, "TaskbarSi"),
        reg_dword(HKEY_CURRENT_USER, adv, "MMTaskbarEnabled"),
    ));

    // monitors
    unsafe extern "system" fn mon_cb(m: HMONITOR, _: HDC, _: *mut RECT, _: LPARAM) -> BOOL {
        let mut mi = MONITORINFOEXW::default();
        mi.monitorInfo.cbSize = std::mem::size_of::<MONITORINFOEXW>() as u32;
        if GetMonitorInfoW(m, &mut mi as *mut _ as *mut MONITORINFO).as_bool() {
            let (mut x, mut y) = (0u32, 0u32);
            let _ = GetDpiForMonitor(m, MDT_EFFECTIVE_DPI, &mut x, &mut y);
            log(format!(
                "Monitor {} primary={} bounds={} work={} dpi={} ({}%)",
                from_wide(&mi.szDevice),
                (mi.monitorInfo.dwFlags & MONITORINFOF_PRIMARY) != 0,
                fmt_rect(&mi.monitorInfo.rcMonitor),
                fmt_rect(&mi.monitorInfo.rcWork),
                x,
                x * 100 / 96
            ));
        }
        BOOL(1)
    }
    unsafe {
        let _ = EnumDisplayMonitors(None, None, Some(mon_cb), LPARAM(0));
    }

    // taskbar
    unsafe {
        let mut abd = APPBARDATA {
            cbSize: std::mem::size_of::<APPBARDATA>() as u32,
            ..Default::default()
        };
        let state = SHAppBarMessage(ABM_GETSTATE, &mut abd);
        let mut abd2 = APPBARDATA {
            cbSize: std::mem::size_of::<APPBARDATA>() as u32,
            ..Default::default()
        };
        SHAppBarMessage(ABM_GETTASKBARPOS, &mut abd2);
        log(format!(
            "Taskbar: edge={} rect={} autohide={}",
            ["left", "top", "right", "bottom"]
                .get(abd2.uEdge as usize)
                .unwrap_or(&"?"),
            fmt_rect(&abd2.rc),
            (state & ABS_AUTOHIDE as usize) != 0
        ));
    }
}

fn log_shell_windows(reason: &str) {
    log(format!("--- top-level shell windows ({reason}) ---"));
    unsafe extern "system" fn cb(h: HWND, _: LPARAM) -> BOOL {
        let p = proc_name(pid_of(h));
        let c = class_of(h);
        if is_shell_host(&p) || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" {
            log(format!(
                "  {:?} {} class='{}' title='{}' rect={} visible={} cloaked={}",
                h.0,
                p,
                c,
                title_of(h),
                fmt_rect(&rect_of(h)),
                visible(h),
                cloaked(h)
            ));
        }
        BOOL(1)
    }
    unsafe {
        let _ = EnumWindows(Some(cb), LPARAM(0));
    }
}

// ---------------------------------------------------------------- UIA worker

enum Cmd {
    Watch(isize),
    Unwatch,
    Quit,
}

/// Region the worker found for the calendar, shared with the UI thread.
static CAL_RECT: Mutex<Option<RECT>> = Mutex::new(None);
static MAIN_HWND: OnceLock<isize> = OnceLock::new();
static UI_FONT: std::sync::atomic::AtomicIsize = std::sync::atomic::AtomicIsize::new(0);
const WM_APP_CALRECT: u32 = WM_APP + 1;

#[derive(Clone)]
struct Node {
    depth: usize,
    ctype: i32,
    name: String,
    aid: String,
    class: String,
    fw: String,
    rect: RECT,
    offscreen: bool,
    ecs: Option<i32>,
    toggle: Option<i32>,
    in_cal: bool,
    in_list: bool,
}

fn ctype_name(id: i32) -> &'static str {
    match id {
        50000 => "Button",
        50001 => "Calendar",
        50002 => "CheckBox",
        50003 => "ComboBox",
        50004 => "Edit",
        50005 => "Hyperlink",
        50006 => "Image",
        50007 => "ListItem",
        50008 => "List",
        50009 => "Menu",
        50011 => "MenuItem",
        50012 => "ProgressBar",
        50013 => "RadioButton",
        50014 => "ScrollBar",
        50015 => "Slider",
        50016 => "Spinner",
        50018 => "Tab",
        50019 => "TabItem",
        50020 => "Text",
        50021 => "ToolBar",
        50022 => "ToolTip",
        50023 => "Tree",
        50024 => "TreeItem",
        50025 => "Custom",
        50026 => "Group",
        50027 => "Thumb",
        50028 => "DataGrid",
        50029 => "DataItem",
        50030 => "Document",
        50031 => "SplitButton",
        50032 => "Window",
        50033 => "Pane",
        50034 => "Header",
        50035 => "HeaderItem",
        50036 => "Table",
        50037 => "TitleBar",
        50038 => "Separator",
        50039 => "SemanticZoom",
        50040 => "AppBar",
        _ => "?",
    }
}

fn redact(n: &Node) -> String {
    if n.name.is_empty() {
        return String::new();
    }
    let len = n.name.chars().count();
    if n.in_cal || (!n.in_list && len <= 40) {
        n.name.replace('\n', " ")
    } else {
        format!("<redacted {len} chars>")
    }
}

fn variant_i32(v: &windows::Win32::System::Variant::VARIANT) -> Option<i32> {
    i32::try_from(v).ok()
}

struct Uia {
    uia: IUIAutomation,
    cache: IUIAutomationCacheRequest,
}

impl Uia {
    fn new() -> windows::core::Result<Self> {
        unsafe {
            let uia: IUIAutomation = CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER)?;
            let cache = uia.CreateCacheRequest()?;
            for p in [
                UIA_NamePropertyId,
                UIA_AutomationIdPropertyId,
                UIA_ClassNamePropertyId,
                UIA_ControlTypePropertyId,
                UIA_BoundingRectanglePropertyId,
                UIA_IsOffscreenPropertyId,
                UIA_FrameworkIdPropertyId,
                UIA_IsExpandCollapsePatternAvailablePropertyId,
                UIA_ExpandCollapseExpandCollapseStatePropertyId,
                UIA_IsTogglePatternAvailablePropertyId,
                UIA_ToggleToggleStatePropertyId,
            ] {
                cache.AddProperty(p)?;
            }
            cache.SetTreeScope(TreeScope_Subtree)?;
            cache.SetTreeFilter(&uia.RawViewCondition()?)?;
            Ok(Self { uia, cache })
        }
    }

    fn snapshot(&self, hwnd: HWND) -> windows::core::Result<Vec<Node>> {
        unsafe {
            let root = self.uia.ElementFromHandleBuildCache(hwnd, &self.cache)?;
            let mut out = vec![];
            walk(&root, 0, false, false, &mut out);
            Ok(out)
        }
    }
}

unsafe fn walk(e: &IUIAutomationElement, depth: usize, in_cal: bool, in_list: bool, out: &mut Vec<Node>) {
    if depth > 40 || out.len() > 3000 {
        return;
    }
    let ctype = e.CachedControlType().map(|c| c.0).unwrap_or(0);
    let name = e.CachedName().map(|b| b.to_string()).unwrap_or_default();
    let aid = e.CachedAutomationId().map(|b| b.to_string()).unwrap_or_default();
    let class = e.CachedClassName().map(|b| b.to_string()).unwrap_or_default();
    let fw = e.CachedFrameworkId().map(|b| b.to_string()).unwrap_or_default();
    let rect = e.CachedBoundingRectangle().unwrap_or_default();
    let offscreen = e.CachedIsOffscreen().map(|b| b.as_bool()).unwrap_or(false);
    let has_ec = e
        .GetCachedPropertyValue(UIA_IsExpandCollapsePatternAvailablePropertyId)
        .ok()
        .and_then(|v| bool::try_from(&v).ok())
        .unwrap_or(false);
    let ecs = if has_ec {
        e.GetCachedPropertyValue(UIA_ExpandCollapseExpandCollapseStatePropertyId)
            .ok()
            .and_then(|v| variant_i32(&v))
    } else {
        None
    };
    let has_t = e
        .GetCachedPropertyValue(UIA_IsTogglePatternAvailablePropertyId)
        .ok()
        .and_then(|v| bool::try_from(&v).ok())
        .unwrap_or(false);
    let toggle = if has_t {
        e.GetCachedPropertyValue(UIA_ToggleToggleStatePropertyId)
            .ok()
            .and_then(|v| variant_i32(&v))
    } else {
        None
    };
    let lc = format!("{} {}", aid, class).to_lowercase();
    let now_cal = in_cal || ctype == 50001 || lc.contains("calendar");
    let now_list = (in_list || ctype == 50008 || ctype == 50007) && !now_cal;
    out.push(Node {
        depth,
        ctype,
        name,
        aid,
        class,
        fw,
        rect,
        offscreen,
        ecs,
        toggle,
        in_cal: now_cal,
        in_list: now_list,
    });
    if let Ok(children) = e.GetCachedChildren() {
        let n = children.Length().unwrap_or(0);
        for i in 0..n {
            if let Ok(c) = children.GetElement(i) {
                walk(&c, depth + 1, now_cal, now_list, out);
            }
        }
    }
}

fn node_line(n: &Node) -> String {
    let mut s = format!(
        "{}{} aid='{}' class='{}' name='{}' rect={}",
        "  ".repeat(n.depth),
        ctype_name(n.ctype),
        n.aid,
        n.class,
        redact(n),
        fmt_rect(&n.rect)
    );
    if n.offscreen {
        s.push_str(" OFFSCREEN");
    }
    if let Some(e) = n.ecs {
        s.push_str(&format!(
            " expandCollapse={}",
            ["Collapsed", "Expanded", "PartiallyExpanded", "LeafNode"]
                .get(e as usize)
                .unwrap_or(&"?")
        ));
    }
    if let Some(t) = n.toggle {
        s.push_str(&format!(" toggle={}", ["Off", "On", "Indeterminate"].get(t as usize).unwrap_or(&"?")));
    }
    if n.depth == 0 {
        s.push_str(&format!(" framework='{}'", n.fw));
    }
    s
}

fn is_key(n: &Node) -> bool {
    // match on AutomationId only (classes like CalendarViewDayItem would flood it)
    let lc = n.aid.to_lowercase();
    n.ctype == 50001
        || n.class == "CalendarView"
        || lc.contains("calendar")
        || lc.contains("expandcollapse")
        || lc.contains("clock")
        || lc.contains("focus")
        || (n.ecs.is_some() && !n.in_list)
}

fn rect_nonzero(r: &RECT) -> bool {
    r.right > r.left && r.bottom > r.top
}

fn union(a: Option<RECT>, b: &RECT) -> Option<RECT> {
    Some(match a {
        None => *b,
        Some(a) => RECT {
            left: a.left.min(b.left),
            top: a.top.min(b.top),
            right: a.right.max(b.right),
            bottom: a.bottom.max(b.bottom),
        },
    })
}

fn worker(rx: Receiver<Cmd>) {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    }
    let uia = match Uia::new() {
        Ok(u) => u,
        Err(e) => {
            log(format!("UIA init failed: {e}"));
            return;
        }
    };

    // taskbar dump (tray region only: skip app buttons for privacy)
    let tray = unsafe { FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) };
    let mut clock_candidates: Vec<IUIAutomationElement> = vec![];
    if let Ok(tray) = tray {
        let tr = rect_of(tray);
        let dpi = dpi_for_rect(&tr) as i32;
        let cutoff = tr.right - 700 * dpi / 96;
        log("--- taskbar UIA (right-hand tray region only) ---");
        match uia.snapshot(tray) {
            Ok(nodes) => {
                for n in nodes.iter().filter(|n| n.depth == 0 || n.rect.left >= cutoff) {
                    log(node_line(n));
                }
            }
            Err(e) => log(format!("taskbar snapshot failed: {e}")),
        }
        // keep live refs to clock-like elements to track their state
        unsafe {
            if let (Ok(root), Ok(cond)) = (
                uia.uia.ElementFromHandle(tray),
                uia.uia.CreateTrueCondition(),
            ) {
                if let Ok(all) = root.FindAll(TreeScope_Descendants, &cond) {
                    let n = all.Length().unwrap_or(0);
                    for i in 0..n {
                        if let Ok(e) = all.GetElement(i) {
                            let aid = e.CurrentAutomationId().map(|b| b.to_string()).unwrap_or_default();
                            let cls = e.CurrentClassName().map(|b| b.to_string()).unwrap_or_default();
                            let lc = format!("{aid} {cls}").to_lowercase();
                            let r = e.CurrentBoundingRectangle().unwrap_or_default();
                            if (lc.contains("clock") || lc.contains("datetime") || lc.contains("notification"))
                                && r.left >= cutoff
                            {
                                log(format!("clock/notification candidate: aid='{aid}' class='{cls}' rect={}", fmt_rect(&r)));
                                clock_candidates.push(e);
                            }
                        }
                    }
                }
            }
        }
    }

    let read_clock_states = |c: &Vec<IUIAutomationElement>| -> String {
        let mut parts = vec![];
        for e in c {
            unsafe {
                let aid = e.CurrentAutomationId().map(|b| b.to_string()).unwrap_or_default();
                let ecs = e
                    .GetCurrentPropertyValue(UIA_ExpandCollapseExpandCollapseStatePropertyId)
                    .ok()
                    .and_then(|v| variant_i32(&v));
                let tog = e
                    .GetCurrentPropertyValue(UIA_ToggleToggleStatePropertyId)
                    .ok()
                    .and_then(|v| variant_i32(&v));
                parts.push(format!("{aid}:ecs={ecs:?},toggle={tog:?}"));
            }
        }
        parts.join(" ")
    };
    let mut last_clock = read_clock_states(&clock_candidates);
    log(format!("taskbar clock state at start: {last_clock}"));

    let mut watching: Option<HWND> = None;
    let mut last_sig = String::new();
    let mut full_dumps = 0u32;
    let mut samples = 0u64;
    let mut stable = 0u32;
    let mut last_dumped = String::new();

    loop {
        let cmd = if watching.is_some() {
            match rx.recv_timeout(Duration::from_millis(120)) {
                Ok(c) => Some(c),
                Err(RecvTimeoutError::Timeout) => None,
                Err(_) => break,
            }
        } else {
            match rx.recv() {
                Ok(c) => Some(c),
                Err(_) => break,
            }
        };
        match cmd {
            Some(Cmd::Quit) => break,
            Some(Cmd::Unwatch) => {
                if watching.is_some() {
                    log(format!("worker: stop watching after {samples} samples"));
                }
                watching = None;
                *CAL_RECT.lock().unwrap() = None;
            }
            Some(Cmd::Watch(h)) => {
                watching = Some(HWND(h as *mut _));
                last_sig.clear();
                samples = 0;
                stable = 0;
            }
            None => {}
        }
        let Some(h) = watching else { continue };

        // sample the flyout
        let t0 = Instant::now();
        let nodes = match uia.snapshot(h) {
            Ok(n) => n,
            Err(e) => {
                log(format!("worker: snapshot failed: {e}"));
                watching = None;
                continue;
            }
        };
        let cost = t0.elapsed().as_millis();
        samples += 1;

        let clk = read_clock_states(&clock_candidates);
        if clk != last_clock {
            log(format!("TASKBAR CLOCK STATE: {clk}"));
            last_clock = clk;
        }

        let keys: Vec<&Node> = nodes.iter().filter(|n| is_key(n)).collect();
        let sig = keys
            .iter()
            .map(|n| {
                format!(
                    "{}|{}|{}|{}|off={}|ecs={:?}|tog={:?}",
                    if n.aid.is_empty() { &n.class } else { &n.aid },
                    ctype_name(n.ctype),
                    // names only for the expand/collapse button: a clock with
                    // seconds would otherwise change the signature every second
                    if n.aid.to_lowercase().contains("expandcollapse") { redact(n) } else { String::new() },
                    fmt_rect(&n.rect),
                    n.offscreen,
                    n.ecs,
                    n.toggle
                )
            })
            .collect::<Vec<_>>()
            .join("\n");
        let root_rect = nodes.first().map(|n| n.rect).unwrap_or_default();
        let sig_full = format!("root={}\n{}", fmt_rect(&root_rect), sig);
        if sig_full == last_sig {
            stable += 1;
        } else {
            stable = 0;
        }
        // full dump once a state has settled (2 identical samples) and it differs
        // from the last dumped state, so open/close animations don't eat the budget
        if stable == 2 && sig_full != last_dumped && full_dumps < 10 {
            full_dumps += 1;
            last_dumped = sig_full.clone();
            let body = nodes.iter().map(node_line).collect::<Vec<_>>().join("\n");
            log(format!("FULL UIA DUMP #{full_dumps} (settled state):\n{body}"));
        }
        if sig_full != last_sig {
            log(format!(
                "STATE CHANGE (sample #{samples}, {} nodes, snapshot {cost} ms):\n{sig_full}",
                nodes.len()
            ));
            last_sig = sig_full;
            // calendar region for the test companion
            let mut region: Option<RECT> = None;
            for n in nodes.iter() {
                if n.offscreen || !rect_nonzero(&n.rect) {
                    continue;
                }
                let lc = n.aid.to_lowercase();
                if n.ctype == 50001
                    || n.class == "CalendarView"
                    || lc.contains("calendar")
                    || lc.contains("expandcollapse")
                {
                    region = union(region, &n.rect);
                }
            }
            *CAL_RECT.lock().unwrap() = region.or(Some(root_rect));
            if let Some(m) = MAIN_HWND.get() {
                unsafe {
                    let _ = PostMessageW(Some(HWND(*m as *mut _)), WM_APP_CALRECT, WPARAM(0), LPARAM(0));
                }
            }
        }
    }
    unsafe { CoUninitialize() };
}

// ---------------------------------------------------------------- UI thread state

struct State {
    tx: Sender<Cmd>,
    main: HWND,
    test: HWND,
    flyout: Option<HWND>,
    flyout_opened_at: Option<Instant>,
    last_rects: HashMap<isize, String>,
    pending_click: Option<(Instant, char)>,
    drag_from: Option<POINT>,
    drag_logged: bool,
    opens: u32,
}

thread_local! {
    static STATE: RefCell<Option<State>> = RefCell::new(None);
}

fn with_state<R>(f: impl FnOnce(&mut State) -> R) -> Option<R> {
    STATE.with(|s| s.borrow_mut().as_mut().map(f))
}

fn flyout_still_open(h: HWND) -> bool {
    (unsafe { IsWindow(Some(h)).as_bool() }) && visible(h) && cloaked(h) == 0
}

fn flyout_open(h: HWND, via: &str) {
    let already = with_state(|s| s.flyout == Some(h)).unwrap_or(false);
    if already {
        return;
    }
    let r = rect_of(h);
    let n = with_state(|s| {
        s.flyout = Some(h);
        s.flyout_opened_at = Some(Instant::now());
        s.opens += 1;
        let _ = s.tx.send(Cmd::Watch(h.0 as isize));
        s.opens
    })
    .unwrap_or(0);
    log(format!(
        "==> FLYOUT OPEN #{n} via {via}: hwnd={:?} {} class='{}' title='{}' rect={} dpi={}",
        h.0,
        proc_name(pid_of(h)),
        class_of(h),
        title_of(h),
        fmt_rect(&r),
        dpi_for_rect(&r)
    ));
}

fn flyout_closed(h: HWND, via: &str) {
    let info = with_state(|s| {
        if s.flyout != Some(h) {
            return None;
        }
        s.flyout = None;
        let _ = s.tx.send(Cmd::Unwatch);
        unsafe {
            let _ = ShowWindow(s.test, SW_HIDE);
        }
        let open_ms = s.flyout_opened_at.map(|t| t.elapsed().as_millis()).unwrap_or(0);
        let click = s.pending_click.map(|(t, z)| (t.elapsed().as_millis(), z));
        Some((open_ms, click))
    })
    .flatten();
    if let Some((open_ms, click)) = info {
        let mut line = format!("<== FLYOUT CLOSED via {via} after {open_ms} ms");
        if let Some((ms, z)) = click {
            if ms < 1500 {
                line.push_str(&format!(" | {ms} ms after a zone {z} click"));
            }
        }
        log(line);
    }
}

unsafe extern "system" fn win_event(
    _hook: HWINEVENTHOOK,
    event: u32,
    hwnd: HWND,
    id_object: i32,
    id_child: i32,
    _thread: u32,
    _time: u32,
) {
    if hwnd.is_invalid() || id_object != OBJID_WINDOW.0 || id_child != CHILDID_SELF as i32 {
        return;
    }
    if GetAncestor(hwnd, GA_ROOT) != hwnd {
        return;
    }
    let pname = proc_name(pid_of(hwnd));
    let class = class_of(hwnd);
    let shell = is_shell_host(&pname);
    let ev = match event {
        EVENT_SYSTEM_FOREGROUND => "FOREGROUND",
        EVENT_OBJECT_SHOW => "SHOW",
        EVENT_OBJECT_HIDE => "HIDE",
        EVENT_OBJECT_CLOAKED => "CLOAKED",
        EVENT_OBJECT_UNCLOAKED => "UNCLOAKED",
        EVENT_OBJECT_LOCATIONCHANGE => "LOCATION",
        _ => "?",
    };

    if shell {
        let r = rect_of(hwnd);
        if event == EVENT_OBJECT_LOCATIONCHANGE {
            let key = hwnd.0 as isize;
            let rs = fmt_rect(&r);
            let changed = with_state(|s| {
                let prev = s.last_rects.insert(key, rs.clone());
                prev.as_deref() != Some(rs.as_str())
            })
            .unwrap_or(true);
            if !changed {
                return;
            }
        }
        log(format!(
            "{ev:<10} {pname} hwnd={:?} class='{class}' title='{}' rect={} visible={} cloaked={}",
            hwnd.0,
            title_of(hwnd),
            fmt_rect(&r),
            visible(hwnd),
            cloaked(hwnd)
        ));
        let big = (r.right - r.left) > 150 && (r.bottom - r.top) > 60;
        match event {
            EVENT_SYSTEM_FOREGROUND if big => flyout_open(hwnd, "foreground"),
            EVENT_OBJECT_UNCLOAKED | EVENT_OBJECT_SHOW if big && visible(hwnd) && cloaked(hwnd) == 0 => {
                flyout_open(hwnd, ev)
            }
            EVENT_OBJECT_CLOAKED | EVENT_OBJECT_HIDE => flyout_closed(hwnd, ev),
            _ => {}
        }
        return;
    }

    // non-shell processes: only foreground changes (process + class, no titles)
    if event == EVENT_SYSTEM_FOREGROUND {
        log(format!("FOREGROUND -> {pname} class='{class}'"));
        let f = with_state(|s| s.flyout).flatten();
        if let Some(f) = f {
            if flyout_still_open(f) {
                log("   (flyout still visible after foreground moved)");
            } else {
                flyout_closed(f, "foreground moved");
            }
        }
    } else if pname == "explorer.exe"
        && (class == "Shell_TrayWnd" || class == "Shell_SecondaryTrayWnd")
        && event != EVENT_OBJECT_LOCATIONCHANGE
    {
        log(format!("{ev:<10} explorer class='{class}' rect={}", fmt_rect(&rect_of(hwnd))));
    }
}

// ---------------------------------------------------------------- test companion window

fn position_test_window() {
    let Some(region) = *CAL_RECT.lock().unwrap() else { return };
    with_state(|s| {
        if s.flyout.is_none() {
            return;
        }
        let dpi = dpi_for_rect(&region) as i32;
        let gap = 12 * dpi / 96;
        let w = 340 * dpi / 96;
        let h = (region.bottom - region.top).max(140 * dpi / 96);
        let x = region.left - gap - w;
        let y = region.bottom - h;
        log(format!(
            "test companion -> {} (calendar region {})",
            fmt_rect(&RECT { left: x, top: y, right: x + w, bottom: y + h }),
            fmt_rect(&region)
        ));
        unsafe {
            let _ = SetWindowPos(
                s.test,
                Some(HWND_TOPMOST),
                x,
                y,
                w,
                h,
                SWP_NOACTIVATE | SWP_SHOWWINDOW,
            );
            let _ = InvalidateRect(Some(s.test), None, true);
        }
    });
}

fn zone_at(hwnd: HWND, pt: POINT) -> char {
    let mut rc = RECT::default();
    unsafe {
        let _ = GetClientRect(hwnd, &mut rc);
    }
    if pt.y < rc.bottom / 2 { 'A' } else { 'B' }
}

unsafe extern "system" fn test_proc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_MOUSEACTIVATE => {
            let mut pt = POINT::default();
            let _ = GetCursorPos(&mut pt);
            let _ = ScreenToClient(hwnd, &mut pt);
            let z = zone_at(hwnd, pt);
            log(format!("test: WM_MOUSEACTIVATE zone {z} -> {}", if z == 'A' { "MA_NOACTIVATE" } else { "MA_ACTIVATE" }));
            LRESULT(if z == 'A' { MA_NOACTIVATE as isize } else { MA_ACTIVATE as isize })
        }
        WM_LBUTTONDOWN => {
            let pt = POINT { x: (lp.0 & 0xFFFF) as i16 as i32, y: ((lp.0 >> 16) & 0xFFFF) as i16 as i32 };
            let z = zone_at(hwnd, pt);
            let open = with_state(|s| s.flyout.map(flyout_still_open).unwrap_or(false)).unwrap_or(false);
            log(format!("TEST CLICK zone {z} at ({},{}) flyout_open_before={open}", pt.x, pt.y));
            with_state(|s| {
                s.pending_click = Some((Instant::now(), z));
                s.drag_from = Some(pt);
                s.drag_logged = false;
            });
            SetCapture(hwnd);
            if z == 'B' {
                // simulate what focusing a search box would do: take activation
                let ok = SetForegroundWindow(hwnd).as_bool();
                log(format!("test: zone B SetForegroundWindow -> {ok}"));
            }
            SetTimer(Some(hwnd), 1, 1500, None);
            LRESULT(0)
        }
        WM_MOUSEMOVE => {
            let pt = POINT { x: (lp.0 & 0xFFFF) as i16 as i32, y: ((lp.0 >> 16) & 0xFFFF) as i16 as i32 };
            with_state(|s| {
                if let Some(f) = s.drag_from {
                    if !s.drag_logged && ((pt.x - f.x).abs() > 10 || (pt.y - f.y).abs() > 10) {
                        s.drag_logged = true;
                        log("TEST DRAG started (mouse captured, window not activated)");
                    }
                }
            });
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            let _ = ReleaseCapture();
            let pt = POINT { x: (lp.0 & 0xFFFF) as i16 as i32, y: ((lp.0 >> 16) & 0xFFFF) as i16 as i32 };
            let open = with_state(|s| {
                let d = s.drag_from.take().map(|f| ((pt.x - f.x).pow(2) + (pt.y - f.y).pow(2)) as f64).unwrap_or(0.0).sqrt();
                (d, s.flyout.map(flyout_still_open).unwrap_or(false))
            });
            if let Some((d, open)) = open {
                log(format!("TEST button up, moved {d:.0}px, flyout_open={open}"));
            }
            LRESULT(0)
        }
        WM_TIMER if wp.0 == 1 => {
            let _ = KillTimer(Some(hwnd), 1);
            let r = with_state(|s| {
                let z = s.pending_click.take().map(|(_, z)| z).unwrap_or('?');
                let open = s.flyout.map(flyout_still_open).unwrap_or(false);
                (z, open)
            });
            if let Some((z, open)) = r {
                let fg = GetForegroundWindow();
                log(format!(
                    "RESULT: 1.5 s after zone {z} click the calendar flyout is {} (foreground: {} '{}')",
                    if open { "STILL OPEN" } else { "CLOSED" },
                    proc_name(pid_of(fg)),
                    class_of(fg)
                ));
            }
            LRESULT(0)
        }
        WM_ACTIVATE => {
            log(format!("test: WM_ACTIVATE state={}", wp.0 & 0xFFFF));
            DefWindowProcW(hwnd, msg, wp, lp)
        }
        WM_PAINT => {
            let mut ps = PAINTSTRUCT::default();
            let hdc = BeginPaint(hwnd, &mut ps);
            let mut rc = RECT::default();
            let _ = GetClientRect(hwnd, &mut rc);
            let mid = rc.bottom / 2;
            let a = RECT { bottom: mid, ..rc };
            let b = RECT { top: mid, ..rc };
            let ga = CreateSolidBrush(COLORREF(0x0032_7D2E)); // green (BGR)
            let gb = CreateSolidBrush(COLORREF(0x0000_6CE0)); // orange (BGR)
            FillRect(hdc, &a, ga);
            FillRect(hdc, &b, gb);
            let _ = DeleteObject(ga.into());
            let _ = DeleteObject(gb.into());
            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, COLORREF(0x00FF_FFFF));
            let font = UI_FONT.load(std::sync::atomic::Ordering::Relaxed);
            if font != 0 {
                SelectObject(hdc, HGDIOBJ(font as *mut _));
            }
            let mut ta = wide("A  (green)\nClick 3 times here, then click-and-drag.\nShould NOT close the calendar.");
            let mut tb = wide("B  (orange)\nClick once here.\nThis one activates, expected to close it.");
            let mut ra = RECT { left: a.left + 12, top: a.top + 10, right: a.right - 12, bottom: a.bottom };
            let mut rb = RECT { left: b.left + 12, top: b.top + 10, right: b.right - 12, bottom: b.bottom };
            let la = ta.len() - 1;
            DrawTextW(hdc, &mut ta[..la], &mut ra, DT_LEFT | DT_WORDBREAK);
            let lb = tb.len() - 1;
            DrawTextW(hdc, &mut tb[..lb], &mut rb, DT_LEFT | DT_WORDBREAK);
            let _ = EndPaint(hwnd, &ps);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wp, lp),
    }
}

// ---------------------------------------------------------------- main window

const ID_FINISH: usize = 101;

unsafe extern "system" fn main_proc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_APP_CALRECT => {
            position_test_window();
            LRESULT(0)
        }
        WM_COMMAND if (wp.0 & 0xFFFF) == ID_FINISH => {
            log("USER clicked Finish");
            let _ = DestroyWindow(hwnd);
            LRESULT(0)
        }
        WM_CTLCOLORSTATIC => {
            let hdc = HDC(wp.0 as *mut _);
            SetBkMode(hdc, TRANSPARENT);
            LRESULT(GetSysColorBrush(COLOR_WINDOW).0 as isize)
        }
        WM_DESTROY => {
            log_shell_windows("at exit");
            log("probe exiting");
            with_state(|s| {
                let _ = s.tx.send(Cmd::Quit);
            });
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wp, lp),
    }
}

fn main() {
    init_log();
    std::panic::set_hook(Box::new(|p| log(format!("PANIC: {p}"))));
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
    log_system_info();
    log_shell_windows("at start");

    let (tx, rx) = channel::<Cmd>();

    unsafe {
        let hinst: HINSTANCE = GetModuleHandleW(None).unwrap().into();

        // fonts
        let mut ncm = NONCLIENTMETRICSW {
            cbSize: std::mem::size_of::<NONCLIENTMETRICSW>() as u32,
            ..Default::default()
        };
        let _ = SystemParametersInfoW(
            SPI_GETNONCLIENTMETRICS,
            ncm.cbSize,
            Some(&mut ncm as *mut _ as *mut _),
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
        );

        let main_cls = w!("WTF_ProbeMain");
        let test_cls = w!("WTF_ProbeCompanion");
        RegisterClassExW(&WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            lpfnWndProc: Some(main_proc),
            hInstance: hinst,
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            hbrBackground: GetSysColorBrush(COLOR_WINDOW),
            lpszClassName: main_cls,
            ..Default::default()
        });
        RegisterClassExW(&WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            lpfnWndProc: Some(test_proc),
            hInstance: hinst,
            hCursor: LoadCursorW(None, IDC_HAND).unwrap_or_default(),
            lpszClassName: test_cls,
            ..Default::default()
        });

        let main = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            main_cls,
            w!("World Time Flyout - probe"),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            100,
            100,
            None,
            None,
            Some(hinst),
            None,
        )
        .expect("main window");
        let _ = MAIN_HWND.set(main.0 as isize);
        let dpi = GetDpiForWindow(main) as i32;
        let s = |v: i32| v * dpi / 96;

        let mut lf = ncm.lfMessageFont;
        lf.lfHeight = -s(15);
        let font = CreateFontIndirectW(&lf);
        UI_FONT.store(font.0 as isize, std::sync::atomic::Ordering::Relaxed);

        let log_path = LOG_PATH.get().map(|p| p.display().to_string()).unwrap_or_default();
        let text = format!(
            "Probe is running. Log file:\r\n{log_path}\r\n\r\n\
             Do these in order, at a normal pace:\r\n\r\n\
             1.  Optional: clear your notifications first, keeps the log clean.\r\n\
             2.  Click the taskbar clock. Wait 2 sec. Click the clock again to close.\r\n\
             3.  Click the clock. Collapse the calendar with its arrow button. Wait 2 sec. Close.\r\n\
             4.  Click the clock (it opens collapsed). Expand it again. Wait 2 sec. Close.\r\n\
             5.  Click the clock. A green/orange test box appears left of the calendar.\r\n\
             \u{20}     Click 3 times in GREEN, then click-and-drag in GREEN.\r\n\
             6.  If the calendar closed, open it again. Click ORANGE once.\r\n\
             7.  Press Win+N. Wait 2 sec. Press Esc.\r\n\
             8.  Click Finish, then tell Claude it's done.\r\n\r\n\
             Nothing is changed on your PC. The log only records window and\r\n\
             layout info for the clock flyout (notification text is redacted)."
        );
        let tw = wide(&text);
        let lbl = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("STATIC"),
            PCWSTR(tw.as_ptr()),
            WS_CHILD | WS_VISIBLE,
            s(16),
            s(14),
            s(600),
            s(400),
            Some(main),
            None,
            Some(hinst),
            None,
        )
        .unwrap();
        let btn = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("BUTTON"),
            w!("Finish"),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_DEFPUSHBUTTON as u32),
            s(16 + 600 - 110),
            s(420),
            s(110),
            s(32),
            Some(main),
            Some(HMENU(ID_FINISH as *mut _)),
            Some(hinst),
            None,
        )
        .unwrap();
        for h in [main, lbl, btn] {
            SendMessageW(h, WM_SETFONT, Some(WPARAM(font.0 as usize)), Some(LPARAM(1)));
        }
        // size the client area
        let mut rc = RECT { left: 0, top: 0, right: s(632), bottom: s(468) };
        let _ = AdjustWindowRectExForDpi(
            &mut rc,
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
            false,
            WINDOW_EX_STYLE(0),
            dpi as u32,
        );
        let _ = SetWindowPos(main, None, 0, 0, rc.right - rc.left, rc.bottom - rc.top, SWP_NOMOVE | SWP_NOZORDER);

        let test = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            test_cls,
            w!("probe companion"),
            WS_POPUP,
            0,
            0,
            10,
            10,
            None,
            None,
            Some(hinst),
            None,
        )
        .expect("test window");
        let pref = DWMWCP_ROUND;
        let _ = DwmSetWindowAttribute(
            test,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            &pref as *const _ as *const _,
            std::mem::size_of_val(&pref) as u32,
        );

        STATE.with(|st| {
            *st.borrow_mut() = Some(State {
                tx: tx.clone(),
                main,
                test,
                flyout: None,
                flyout_opened_at: None,
                last_rects: HashMap::new(),
                pending_click: None,
                drag_from: None,
                drag_logged: false,
                opens: 0,
            })
        });

        let worker_handle = std::thread::spawn(move || worker(rx));

        let flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        let mut hooks = vec![];
        for (a, b) in [
            (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
            (EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE),
            (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
            (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
        ] {
            let h = SetWinEventHook(a, b, None, Some(win_event), 0, 0, flags);
            log(format!("WinEvent hook {a:#x}-{b:#x}: {}", if h.is_invalid() { "FAILED" } else { "ok" }));
            hooks.push(h);
        }

        let _ = ShowWindow(main, SW_SHOW);
        log("ready - waiting for the user to open the calendar");

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        for h in hooks {
            let _ = UnhookWinEvent(h);
        }
        drop(tx);
        let _ = worker_handle.join();
        log("done");
    }
}
