// World Time Flyout: first native Windows companion prototype.
// No network access or injected DLLs. Uses only Win32 and GDI.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <cstring>
#include <iterator>
#include <dwmapi.h>
#include <shellapi.h>
#include <algorithm>
#include <array>
#include <string>
#include <vector>
#include <cwchar>
#include <cmath>

#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "shell32.lib")

namespace {
constexpr UINT MSG_TOGGLE = WM_APP + 30;
constexpr UINT MSG_TRAY = WM_APP + 31;
constexpr UINT TIMER_UPDATE = 1;
constexpr int PANEL_WIDTH = 344;
constexpr int PAGE_SIZE = 3;

struct Place { const wchar_t* name; const wchar_t* zone; };
constexpr Place catalog[] = {
  {L"Yerevan", L"Armenian Standard Time"},
  {L"New York", L"Eastern Standard Time"},
  {L"London", L"GMT Standard Time"},
  {L"Los Angeles", L"Pacific Standard Time"},
  {L"Tokyo", L"Tokyo Standard Time"},
  {L"Paris", L"Romance Standard Time"},
  {L"Berlin", L"W. Europe Standard Time"},
  {L"Dubai", L"Arabian Standard Time"},
  {L"Singapore", L"Singapore Standard Time"},
  {L"Sydney", L"AUS Eastern Standard Time"},
  {L"Mumbai", L"India Standard Time"},
  {L"San Francisco", L"Pacific Standard Time"}
};
HWND windowHandle = nullptr;
HHOOK clockHook = nullptr;
HINSTANCE instance = nullptr;
bool compact = false;
bool followingNow = true;
bool light = false;
int page = 0;
int scrollRows = 0;
int minuteStep = 60;
int dragX = 0;
int dragY = 0;
bool pointerDown = false;
COLORREF accent = RGB(0,120,215);
std::vector<int> selected = {0,1,2,3,4};
ULONGLONG selectedTicks = 0;
std::array<DYNAMIC_TIME_ZONE_INFORMATION, sizeof(catalog)/sizeof(catalog[0])> cachedZones{};
std::array<bool, sizeof(catalog)/sizeof(catalog[0])> zoneAvailable{};

void synchronizeNow() {
  FILETIME ft{};
  GetSystemTimeAsFileTime(&ft);
  selectedTicks = (static_cast<ULONGLONG>(ft.dwHighDateTime)<<32)|ft.dwLowDateTime;
}
void shift(int mins) {
  LONGLONG delta = static_cast<LONGLONG>(mins) * 600000000LL;
  selectedTicks = static_cast<ULONGLONG>(static_cast<LONGLONG>(selectedTicks)+delta);
  followingNow = false;
  InvalidateRect(windowHandle,nullptr,FALSE);
}
SYSTEMTIME utcTime() {
  FILETIME ft{static_cast<DWORD>(selectedTicks),static_cast<DWORD>(selectedTicks>>32)};
  SYSTEMTIME st{}; FileTimeToSystemTime(&ft,&st); return st;
}
void loadZones() {
  for(DWORD i=0;i<1000;++i) {
    DYNAMIC_TIME_ZONE_INFORMATION info{};
    if(EnumDynamicTimeZoneInformation(i,&info)!=ERROR_SUCCESS) break;
    for(size_t j=0;j<std::size(catalog);++j)
      if(wcscmp(info.TimeZoneKeyName,catalog[j].zone)==0) {
        cachedZones[j]=info; zoneAvailable[j]=true;
      }
  }
}
SYSTEMTIME placeTime(int id) {
  SYSTEMTIME utc = utcTime(), local = utc;
  if(id>=0 && id<static_cast<int>(std::size(catalog)) && zoneAvailable[id])
    SystemTimeToTzSpecificLocalTimeEx(&cachedZones[id],&utc,&local);
  return local;
}
std::wstring formattedTime(const SYSTEMTIME& st) {
  wchar_t buffer[96]{};
  if(GetTimeFormatEx(LOCALE_NAME_USER_DEFAULT,TIME_NOSECONDS,&st,nullptr,buffer,96)) return buffer;
  swprintf_s(buffer,L"%02d:%02d",st.wHour,st.wMinute);
  return buffer;
}
std::wstring formattedDate(const SYSTEMTIME& st) {
  wchar_t buffer[96]{};
  if(GetDateFormatEx(LOCALE_NAME_USER_DEFAULT,DATE_SHORTDATE,&st,nullptr,buffer,96,nullptr)) return buffer;
  return {};
}
int height() { return compact ? 184 : 472; }
void updateAppearance() {
  DWORD mode=0, cb=sizeof(mode);
  if(RegGetValueW(HKEY_CURRENT_USER,
    L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
    L"AppsUseLightTheme",RRF_RT_REG_DWORD,nullptr,&mode,&cb)==ERROR_SUCCESS) light=(mode!=0);
  DWORD color=0; BOOL opaque=FALSE;
  if(SUCCEEDED(DwmGetColorizationColor(&color,&opaque)))
    accent=RGB((color>>16)&255,(color>>8)&255,color&255);
  BOOL dark = light?FALSE:TRUE;
  DwmSetWindowAttribute(windowHandle,20,&dark,sizeof(dark));
  int corners=2; DwmSetWindowAttribute(windowHandle,33,&corners,sizeof(corners));
  InvalidateRect(windowHandle,nullptr,FALSE);
}
void positionWindow() {
  RECT taskbar{};
  HWND tb=FindWindowW(L"Shell_TrayWnd",nullptr);
  if(tb) GetWindowRect(tb,&taskbar);
  RECT work{}; SystemParametersInfoW(SPI_GETWORKAREA,0,&work,0);
  int right=work.right-12;
  int bottom=tb?taskbar.top-8:work.bottom-8;
  SetWindowPos(windowHandle,HWND_TOPMOST,right-PANEL_WIDTH,bottom-height(),
    PANEL_WIDTH,height(),SWP_NOACTIVATE);
}
void toggle() {
  if(IsWindowVisible(windowHandle)) ShowWindow(windowHandle,SW_HIDE);
  else { positionWindow(); ShowWindow(windowHandle,SW_SHOWNOACTIVATE); }
}
void copyText(const std::wstring& value) {
  if(!OpenClipboard(windowHandle)) return;
  EmptyClipboard();
  size_t bytes=(value.size()+1)*sizeof(wchar_t);
  HGLOBAL memory=GlobalAlloc(GMEM_MOVEABLE,bytes);
  if(memory) {
    void* dest=GlobalLock(memory);
    if(dest) {
      memcpy(dest,value.c_str(),bytes); GlobalUnlock(memory);
      if(!SetClipboardData(CF_UNICODETEXT,memory)) GlobalFree(memory);
    } else GlobalFree(memory);
  }
  CloseClipboard();
}
void copyVisible() {
  std::wstring out;
  int start=compact?page*PAGE_SIZE:scrollRows;
  int end=std::min(static_cast<int>(selected.size()),start+(compact?PAGE_SIZE:7));
  for(int n=start;n<end;++n) {
    int id=selected[n]; auto st=placeTime(id);
    if(!out.empty()) out+=L"\r\n";
    out+=std::wstring(catalog[id].name)+L" — "+formattedDate(st)+L" "+formattedTime(st);
  }
  copyText(out);
}
void fill(HDC dc, RECT rect, COLORREF color) {
  HBRUSH brush=CreateSolidBrush(color);
  FillRect(dc,&rect,brush);
  DeleteObject(brush);
}
void write(HDC dc,int x,int y,int width,const std::wstring& str,
           int size,bool bold,COLORREF color,bool right=false) {
  HFONT font=CreateFontW(-size,0,0,0,bold?FW_SEMIBOLD:FW_NORMAL,
    FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,
    CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI Variable");
  HGDIOBJ old=SelectObject(dc,font);
  SetBkMode(dc,TRANSPARENT); SetTextColor(dc,color);
  RECT rect{x,y,x+width,y+30};
  DrawTextW(dc,str.c_str(),-1,&rect,DT_SINGLELINE|DT_VCENTER|DT_END_ELLIPSIS|
    (right?DT_RIGHT:DT_LEFT));
  SelectObject(dc,old); DeleteObject(font);
}
void paint(HDC dc) {
  RECT client{};GetClientRect(windowHandle,&client);
  COLORREF bg=light?RGB(246,247,250):RGB(35,36,39);
  COLORREF fg=light?RGB(28,29,33):RGB(245,245,246);
  COLORREF subtle=light?RGB(111,116,125):RGB(177,179,185);
  COLORREF border=light?RGB(218,221,225):RGB(70,72,76);
  fill(dc,client,bg);
  write(dc,15,8,220,L"World Time",17,true,fg);
  write(dc,245,8,86,compact?L"Expand  ⋯":L"Compact  ⋯",12,false,subtle,true);
  RECT line{0,42,PANEL_WIDTH,43};fill(dc,line,border);
  int start=compact?page*PAGE_SIZE:scrollRows;
  int count=compact?PAGE_SIZE:7;
  int rowH=compact?34:49;
  for(int j=0;j<count;++j) {
    int idx=start+j; if(idx>=static_cast<int>(selected.size()))break;
    int id=selected[idx];auto time=placeTime(id);
    int top=50+j*rowH;
    if(j==0) {RECT marker{8,top+5,11,top+rowH-5};fill(dc,marker,accent);}
    write(dc,20,top,150,catalog[id].name,compact?13:14,true,fg);
    write(dc,181,top,149,formattedTime(time),compact?13:15,true,fg,true);
    if(!compact) write(dc,20,top+21,275,formattedDate(time),11,false,subtle);
    RECT sep{19,top+rowH-1,PANEL_WIDTH-19,top+rowH};fill(dc,sep,border);
  }
  int footer=height()-56;
  RECT divider{0,footer-6,PANEL_WIDTH,footer-5};fill(dc,divider,border);
  write(dc,14,footer,30,L"‹",23,false,fg);
  write(dc,45,footer+3,117,formattedTime(placeTime(selected.empty()?0:selected[0])),13,true,fg);
  write(dc,167,footer,30,L"›",23,false,fg);
  write(dc,207,footer+2,58,L"Now",13,false,fg);
  write(dc,277,footer+2,60,L"Copy",13,false,fg);
  if(compact) {
    std::wstring pg=std::to_wstring(page+1)+L"/"+
      std::to_wstring(std::max(1,(static_cast<int>(selected.size())+2)/3));
    write(dc,125,height()-22,95,pg,11,false,subtle);
  } else {
    write(dc,14,height()-23,305,
      L"Scroll list • Drag horizontally to shift time • Click + to add",11,false,subtle);
  }
}
void cityMenu() {
  HMENU menu=CreatePopupMenu();
  for(size_t i=0;i<std::size(catalog);++i) {
    bool added=std::find(selected.begin(),selected.end(),static_cast<int>(i))!=selected.end();
    AppendMenuW(menu,MF_STRING|(added?MF_CHECKED:0),1000+static_cast<UINT>(i),catalog[i].name);
  }
  POINT point{};GetCursorPos(&point);
  int cmd=TrackPopupMenu(menu,TPM_RETURNCMD|TPM_RIGHTBUTTON,point.x,point.y,
    0,windowHandle,nullptr);
  DestroyMenu(menu);
  if(cmd>=1000 && cmd<1000+static_cast<int>(std::size(catalog))) {
    int id=cmd-1000;
    auto it=std::find(selected.begin(),selected.end(),id);
    if(it==selected.end()) selected.push_back(id);
    else if(selected.size()>1) selected.erase(it);
    page=0;scrollRows=0;InvalidateRect(windowHandle,nullptr,FALSE);
  }
}
LRESULT CALLBACK proc(HWND hwnd,UINT message,WPARAM wp,LPARAM lp) {
  switch(message) {
    case WM_CREATE: {
      SetTimer(hwnd,TIMER_UPDATE,1000,nullptr);
      return 0;
    }
    case WM_TIMER:
      if(followingNow) {synchronizeNow();InvalidateRect(hwnd,nullptr,FALSE);}
      return 0;
    case WM_PAINT: {
      PAINTSTRUCT ps{};HDC dc=BeginPaint(hwnd,&ps);
      RECT rect{};GetClientRect(hwnd,&rect);
      HDC memory=CreateCompatibleDC(dc);
      HBITMAP bitmap=CreateCompatibleBitmap(dc,rect.right,rect.bottom);
      HGDIOBJ original=SelectObject(memory,bitmap);
      paint(memory);BitBlt(dc,0,0,rect.right,rect.bottom,memory,0,0,SRCCOPY);
      SelectObject(memory,original);DeleteObject(bitmap);DeleteDC(memory);
      EndPaint(hwnd,&ps);return 0;
    }
    case WM_SETTINGCHANGE:
    case WM_DWMCOLORIZATIONCOLORCHANGED: updateAppearance();return 0;
    case WM_LBUTTONDOWN:
      dragX=GET_X_LPARAM(lp);dragY=GET_Y_LPARAM(lp);
      pointerDown=true;SetCapture(hwnd);return 0;
    case WM_LBUTTONUP: {
      int x=GET_X_LPARAM(lp),y=GET_Y_LPARAM(lp);
      if(pointerDown) ReleaseCapture();
      pointerDown=false;
      int dx=x-dragX,dy=y-dragY;
      if(compact && std::abs(dx)>45) {
        int maxPage=std::max(0,(static_cast<int>(selected.size())-1)/3);
        page=std::clamp(page+(dx<0?1:-1),0,maxPage);
      } else if(!compact && std::abs(dx)>30) {
        shift((dx<0?1:-1)*minuteStep*std::max(1,std::abs(dx)/30));
      } else if(y<43) {
        if(x>220 && x<315) {
          compact=!compact;positionWindow();
        } else if(x>=315) cityMenu();
      } else if(y>=height()-64) {
        if(x<43) shift(-minuteStep);
        else if(x<165) { if(compact) page=std::max(0,page-1); else shift(-minuteStep); }
        else if(x<201) { if(compact) page=std::min((static_cast<int>(selected.size())-1)/3,page+1); else shift(minuteStep); }
        else if(x<270) {followingNow=true;synchronizeNow();}
        else copyVisible();
      } else if(y>=50) {
        int idx=(compact?page*3:scrollRows)+(y-50)/(compact?34:49);
        if(idx>=0 && idx<static_cast<int>(selected.size())) {
          auto st=placeTime(selected[idx]);
          copyText(std::wstring(catalog[selected[idx]].name)+L" — "+
            formattedDate(st)+L" "+formattedTime(st));
        }
      }
      InvalidateRect(hwnd,nullptr,FALSE);return 0;
    }
    case WM_MOUSEWHEEL:
      if(compact) page=std::clamp(page+(GET_WHEEL_DELTA_WPARAM(wp)<0?1:-1),
        0,std::max(0,(static_cast<int>(selected.size())-1)/3));
      else scrollRows=std::clamp(scrollRows+(GET_WHEEL_DELTA_WPARAM(wp)<0?1:-1),
        0,std::max(0,static_cast<int>(selected.size())-7));
      InvalidateRect(hwnd,nullptr,FALSE);return 0;
    case WM_RBUTTONUP: cityMenu();return 0;
    case MSG_TOGGLE: toggle();return 0;
    case MSG_TRAY:
      if(LOWORD(lp)==WM_LBUTTONUP) toggle();
      else if(LOWORD(lp)==WM_RBUTTONUP) cityMenu();
      return 0;
    case WM_CLOSE:ShowWindow(hwnd,SW_HIDE);return 0;
    case WM_DESTROY:PostQuitMessage(0);return 0;
  }
  return DefWindowProcW(hwnd,message,wp,lp);
}
LRESULT CALLBACK mouseCallback(int n,WPARAM msg,LPARAM data) {
  if(n==HC_ACTION && msg==WM_LBUTTONUP && windowHandle) {
    const MSLLHOOKSTRUCT* input=reinterpret_cast<const MSLLHOOKSTRUCT*>(data);
    HWND taskbar=FindWindowW(L"Shell_TrayWnd",nullptr);
    RECT rect{};
    if(taskbar && GetWindowRect(taskbar,&rect) &&
       input->pt.y>=rect.top && input->pt.y<rect.bottom &&
       input->pt.x>=rect.right-115 && input->pt.x<rect.right) {
      PostMessageW(windowHandle,MSG_TOGGLE,0,0);
    }
  }
  return CallNextHookEx(clockHook,n,msg,data);
}
}
int WINAPI wWinMain(HINSTANCE h,HINSTANCE,LPWSTR,int) {
  instance=h;
  loadZones();synchronizeNow();
  WNDCLASSEXW clazz{sizeof(clazz)};
  clazz.hInstance=h;clazz.lpfnWndProc=proc;
  clazz.lpszClassName=L"WorldTimeFlyoutCompanion";
  clazz.hCursor=LoadCursorW(nullptr,IDC_ARROW);
  RegisterClassExW(&clazz);
  windowHandle=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_TOPMOST|WS_EX_NOACTIVATE,
    clazz.lpszClassName,L"World Time Flyout",WS_POPUP,
    CW_USEDEFAULT,CW_USEDEFAULT,PANEL_WIDTH,height(),nullptr,nullptr,h,nullptr);
  if(!windowHandle)return 1;
  updateAppearance();positionWindow();
  NOTIFYICONDATAW tray{};
  tray.cbSize=sizeof(tray);tray.hWnd=windowHandle;tray.uID=1;
  tray.uFlags=NIF_MESSAGE|NIF_TIP|NIF_ICON;
  tray.uCallbackMessage=MSG_TRAY;
  tray.hIcon=LoadIconW(nullptr,IDI_INFORMATION);
  wcscpy_s(tray.szTip,L"World Time Flyout");
  Shell_NotifyIconW(NIM_ADD,&tray);
  clockHook=SetWindowsHookExW(WH_MOUSE_LL,mouseCallback,h,0);
  RegisterHotKey(windowHandle,1,MOD_CONTROL|MOD_ALT,'T');
  MSG msg{};
  while(GetMessageW(&msg,nullptr,0,0)>0) {
    if(msg.message==WM_HOTKEY && msg.wParam==1)toggle();
    TranslateMessage(&msg);DispatchMessageW(&msg);
  }
  UnregisterHotKey(windowHandle,1);
  if(clockHook)UnhookWindowsHookEx(clockHook);
  Shell_NotifyIconW(NIM_DELETE,&tray);
  return 0;
}
