#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <algorithm>
#include <cwchar>
#include <cmath>
#include <string>

#include "Displays.h"
static int current = -1;
static HHOOK hook;
static NOTIFYICONDATA tray{sizeof(tray)};
static bool passive;
static bool userPaused;
static bool hotkeyRegistered;
enum class Alignment : int { Active=1, Native, Paused, Unsupported, HookFailed, OtherProfile };
static Alignment alignment=Alignment::Unsupported;
static UINT taskbarCreated;
static bool inside(RECT r, POINT p) {
    return p.x >= r.left && p.x < r.right && p.y >= r.top && p.y < r.bottom;
}
static int locate(POINT p) {
    for (int i=0;i<count();++i) if (inside(panels[i].px,p)) return i;
    return -1;
}
static POINT mapAcross(int from,int to,POINT proposed) {
    const auto &a=panels[from], &b=panels[to];
    double y=a.top+(proposed.y-a.px.top+0.5)*a.mmHeight/a.height;
    if (y<b.top || y>=b.top+b.mmHeight) {
        if(bridgeEdges) return {to>from?b.px.left:b.px.right-1,y<b.top?b.px.top:b.px.bottom-1};
        return {to>from?a.px.right-1:a.px.left,std::clamp(proposed.y,a.px.top,a.px.bottom-1)};
    }
    return {std::clamp(proposed.x,b.px.left,b.px.right-1),
            b.px.top+LONG((y-b.top)*b.height/b.mmHeight)};
}
static LRESULT CALLBACK mouse(int code,WPARAM msg,LPARAM data) {
    if (code==HC_ACTION && msg==WM_MOUSEMOVE) {
        const auto &event=*reinterpret_cast<const MSLLHOOKSTRUCT*>(data);
        POINT p=event.pt;
        if (current<0) current=locate(p);
        else if (event.flags & LLMHF_INJECTED) {
            if (!inside(panels[current].px,p)) current=locate(p);
        }
        else {
            const RECT r=panels[current].px;
            if (p.x<r.left || p.x>=r.right) {
                const int to=current+(p.x<r.left?-1:1);
                if (to>=0 && to<count()) {
                    POINT dest=mapAcross(current,to,p);
                    RECT clip;
                    if (!passive && (dest.x!=p.x || dest.y!=p.y) &&
                        GetClipCursor(&clip) && inside(clip,dest) && SetCursorPos(dest.x,dest.y)) {
                        current=inside(panels[to].px,dest)?to:current;
                        return 1;
                    }
                    current=inside(panels[to].px,p)?to:locate(p);
                }
            }
        }
    }
    return CallNextHookEx(nullptr,code,msg,data);
}
// Only bypass routing for a continuous row with identical vertical pixel
// ranges and physical calibration. Mixed-pitch and offset rows need the hook.
static bool nativeAlignment() {
    const auto &first=panels.front();
    return std::all_of(panels.begin()+1,panels.end(),[&](const Panel& p){
        return p.px.top==first.px.top && p.px.bottom==first.px.bottom &&
               p.top==first.top && p.mmHeight==first.mmHeight;
    });
}
static void configure() {
    if (hook) { UnhookWindowsHookEx(hook); hook=nullptr; }
    bool ready=loadLayout() && rowReady;
    bool native=ready && nativeAlignment();
    if (!userPaused && ready && !native) {
        POINT p;
        current=GetCursorPos(&p)?locate(p):-1;
        hook=SetWindowsHookEx(WH_MOUSE_LL,mouse,GetModuleHandle(nullptr),0);
    }
    alignment=userPaused?Alignment::Paused:!ready?Alignment::Unsupported:native?Alignment::Native:hook?Alignment::Active:Alignment::HookFailed;
    bool shortcut=!passive && number(configPath,L"General",L"PauseShortcut",0,0,1)==1;
    if(shortcut && !hotkeyRegistered)hotkeyRegistered=RegisterHotKey(tray.hWnd,1,MOD_CONTROL|MOD_ALT|MOD_SHIFT|MOD_NOREPEAT,'Q')!=FALSE;
    if(!shortcut && hotkeyRegistered){UnregisterHotKey(tray.hWnd,1);hotkeyRegistered=false;}
    const wchar_t* status=L"QuietMouse – paused (layout or mouse hook unavailable)";
    if(userPaused) status=L"QuietMouse – paused by you";
    else if(native) status=L"QuietMouse – aligned by Windows";
    else if(hook) status=L"QuietMouse – active";
    wcscpy_s(tray.szTip,status);
    if (!passive) Shell_NotifyIcon(NIM_MODIFY,&tray);
    if (!passive) {
        if(HWND editor=FindWindow(nullptr,L"QuietMouse · Display settings")) PostMessage(editor,WM_APP+6,0,0);
    }
}
static void openSettings(HWND owner){
    auto editor=appDir+L"QuietMouse.Settings.exe";
    if(reinterpret_cast<INT_PTR>(ShellExecute(owner,L"open",editor.c_str(),nullptr,nullptr,SW_SHOWNORMAL))<=32)
        MessageBox(owner,L"Keep QuietMouse.Settings.exe in the same folder as QuietMouse.exe.",L"QuietMouse",MB_ICONERROR);
}
static LRESULT CALLBACK windowProc(HWND w,UINT msg,WPARAM a,LPARAM b) {
    if(taskbarCreated && msg==taskbarCreated && !passive && tray.hWnd) {
        Shell_NotifyIcon(NIM_ADD,&tray);
        return 0;
    }
    if (msg==WM_DISPLAYCHANGE) { configure(); return 0; }
    if (msg==WM_APP+2 || msg==WM_APP+3) {
        if(msg==WM_APP+2)configure();
        uint64_t requested=(uint64_t(uint32_t(b))<<32)|uint32_t(a);
        if(requested && (profile.empty() || wcstoull(profile.c_str()+7,nullptr,16)!=requested))return int(Alignment::OtherProfile);
        return int(alignment)|(hotkeyRegistered?256:0);
    }
    if(msg==WM_HOTKEY && a==1){userPaused=!userPaused;configure();return 0;}
    if(msg==WM_APP+4){openSettings(w);return 0;}
    if (msg==WM_APP+1 && (b==WM_RBUTTONUP || b==WM_LBUTTONUP)) {
        HMENU menu=CreatePopupMenu();
        AppendMenu(menu,MF_STRING,3,L"Display settings…");
        AppendMenu(menu,MF_STRING,4,userPaused?L"Resume alignment":L"Pause alignment");
        AppendMenu(menu,MF_STRING,2,L"Reload displays");
        AppendMenu(menu,MF_STRING,1,L"Exit QuietMouse");
        POINT p; GetCursorPos(&p); SetForegroundWindow(w);
        int choice=TrackPopupMenu(menu,TPM_RETURNCMD|TPM_RIGHTBUTTON,p.x,p.y,0,w,nullptr);
        DestroyMenu(menu);
        if(choice==3) openSettings(w);
        if (choice==1) DestroyWindow(w);
        if(choice==2) configure();
        if(choice==4) { userPaused=!userPaused; configure(); }
        return 0;
    }
    if (msg==WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProc(w,msg,a,b);
}
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,PWSTR,int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    wchar_t path[32768];
    DWORD len=GetModuleFileNameW(nullptr,path,32768);
    if (!len || len>=32768) return 4;
    configPath.assign(path,len);
    configPath.resize(configPath.find_last_of(L'\\')+1);
    appDir=configPath;
    wchar_t data[MAX_PATH];
    if(FAILED(SHGetFolderPathW(nullptr,CSIDL_LOCAL_APPDATA,nullptr,0,data)))return 4;
    configPath=std::wstring(data)+L"\\QuietMouse\\Profiles.ini";
    int argc=0;
    auto argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    if (!argv) return 4;
    bool check=argc==2 && !wcscmp(argv[1],L"--check");
    passive=argc==2 && !wcscmp(argv[1],L"--probe");
    bool layout=argc==2 && !wcscmp(argv[1],L"--layout");
    LocalFree(argv);
    if(layout)return writeLayout();
    if (check) {
        if (!loadLayout() || !rowReady) return 2;
        for (int from=0;from<count();++from) {
            for (int step : {-1,1}) {
                int to=from+step;
                if (to<0 || to>=count()) continue;
                const auto &a=panels[from], &b=panels[to];
                LONG edge=step>0?a.px.right:a.px.left-1;
                for (LONG row=0;row<a.height;++row) {
                    POINT p{edge,a.px.top+row};
                    POINT dest=mapAcross(from,to,p);
                    double mm=a.top+(row+0.5)*a.mmHeight/a.height;
                    if (mm<b.top || mm>=b.top+b.mmHeight) {
                        if(bridgeEdges){
                            if(!inside(b.px,dest) || dest.y!=(mm<b.top?b.px.top:b.px.bottom-1)) return 7;
                        }else if (!inside(a.px,dest) || dest.y!=p.y) return 7;
                    } else {
                        if (!inside(b.px,dest)) return 7;
                        POINT back=mapAcross(to,from,{step>0?b.px.left-1:b.px.right,dest.y});
                        if (abs(back.y-p.y)>1+std::ceil((b.mmHeight/b.height)/(a.mmHeight/a.height))) return 7;
                    }
                }
            }
        }
        return 0;
    }
    if (argc>1 && !passive) return 4;
    if(HWND existing=FindWindow(L"QuietMouse.Window",L"QuietMouse")){
        if(passive)return 3;
        if(!PostMessage(existing,WM_APP+4,0,0))openSettings(nullptr);
        return 0;
    }
    HANDLE lock=CreateMutex(nullptr,FALSE,L"Local\\QuietMouse");
    if (!lock || GetLastError()==ERROR_ALREADY_EXISTS) return 3;
    WNDCLASS cls{};
    cls.hInstance=instance; cls.lpfnWndProc=windowProc; cls.lpszClassName=L"QuietMouse.Window";
    if (!RegisterClass(&cls)) return 4;
    HWND w=CreateWindow(cls.lpszClassName,L"QuietMouse",WS_POPUP,0,0,0,0,nullptr,nullptr,instance,nullptr);
    if (!w) return 4;
    tray.hWnd=w; tray.uID=1; tray.uFlags=NIF_ICON|NIF_MESSAGE|NIF_TIP;
    tray.hIcon=static_cast<HICON>(LoadImage(instance,MAKEINTRESOURCE(1),IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON),GetSystemMetrics(SM_CYSMICON),LR_DEFAULTCOLOR));
    tray.uCallbackMessage=WM_APP+1;
    taskbarCreated=RegisterWindowMessageW(L"TaskbarCreated");
    if(taskbarCreated) ChangeWindowMessageFilterEx(w,taskbarCreated,MSGFLT_ALLOW,nullptr);
    if (!passive) Shell_NotifyIcon(NIM_ADD,&tray);
    configure();
    if (passive && !hook && !(rowReady && nativeAlignment())) return 5;
    if (passive && !SetTimer(w,1,10000,nullptr)) return 6;
    MSG m;
    while (GetMessage(&m,nullptr,0,0)>0) {
        if (passive && m.message==WM_TIMER) break;
        DispatchMessage(&m);
    }
    if (hook) UnhookWindowsHookEx(hook);
    Shell_NotifyIcon(NIM_DELETE,&tray);
    CloseHandle(lock);
    return 0;
}
