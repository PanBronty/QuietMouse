#pragma once
#include <vector>
#include <cstdint>
#include <shlobj.h>

struct Panel {
    std::wstring key,model,name;
    LONG width,height;
    double top,mmWidth,mmHeight,detectedHeight;
    RECT px;
    bool estimated;
};
static std::vector<Panel> panels;
static std::wstring appDir,profile,configPath;
static bool rowReady;
static bool bridgeEdges;
static int count(){return static_cast<int>(panels.size());}

static std::wstring fingerprint(const std::wstring& s){
    uint64_t hash=14695981039346656037ULL;
    for(wchar_t c:s){hash^=c;hash*=1099511628211ULL;}
    wchar_t out[17]; swprintf_s(out,L"%016llx",static_cast<unsigned long long>(hash));return out;
}
static double number(const std::wstring& file,const wchar_t* section,const std::wstring& key,double fallback,double lo,double hi){
    wchar_t buf[64];
    if(!GetPrivateProfileStringW(section,key.c_str(),L"",buf,64,file.c_str()))return fallback;
    std::replace(buf,buf+wcslen(buf),L',',L'.');
    wchar_t* end;double v=wcstod(buf,&end);
    return end!=buf && !*end && std::isfinite(v) && v>=lo && v<=hi?v:fallback;
}
static void readEdid(Panel& p,const std::wstring& id){
    auto a=id.find(L'#'),b=id.find(L'#',a+1),c=id.find(L'#',b+1);
    if(a==id.npos || b==id.npos || c==id.npos)return;
    p.model=id.substr(a+1,b-a-1);
    auto reg=L"SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\"+p.model+L"\\"+id.substr(b+1,c-b-1)+L"\\Device Parameters";
    BYTE ed[4096];DWORD bytes=sizeof(ed);
    if(RegGetValueW(HKEY_LOCAL_MACHINE,reg.c_str(),L"EDID",RRF_RT_REG_BINARY,nullptr,ed,&bytes)!=ERROR_SUCCESS || bytes<128)return;
    unsigned sum=0;
    for(int i=0;i<128;++i)sum+=ed[i];
    if(sum%256 || ed[0]!=0 || ed[1]!=255)return;
    for(int j=54;j<=108;j+=18){
        if(ed[j] || ed[j+1]){
            int w=ed[j+12]+((ed[j+14]&240)<<4),h=ed[j+13]+((ed[j+14]&15)<<8);
            if(!p.mmWidth && w && h){p.mmWidth=w;p.mmHeight=h;}
        }else if(ed[j+3]==0xfc){
            std::wstring label;
            for(int k=j+5;k<j+18 && ed[k]>=32 && ed[k]<127;++k)label+=wchar_t(ed[k]);
            if(!label.empty())p.name=label;
        }
    }
    if(!p.mmWidth && ed[21] && ed[22]){p.mmWidth=10*ed[21];p.mmHeight=10*ed[22];}
}
static bool loadLayout(){
    panels.clear();rowReady=false;
    for(DWORD i=0;;++i){
        DISPLAY_DEVICE adapter{sizeof(adapter)};
        if(!EnumDisplayDevices(nullptr,i,&adapter,0))break;
        if(!(adapter.StateFlags&DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) || (adapter.StateFlags&DISPLAY_DEVICE_MIRRORING_DRIVER))continue;
        DISPLAY_DEVICE monitor{sizeof(monitor)};
        DEVMODE mode{};mode.dmSize=sizeof(mode);
        if(!EnumDisplaySettings(adapter.DeviceName,ENUM_CURRENT_SETTINGS,&mode))return false;
        EnumDisplayDevices(adapter.DeviceName,0,&monitor,EDD_GET_DEVICE_INTERFACE_NAME);
        std::wstring id=monitor.DeviceID;
        Panel p{};p.name=monitor.DeviceString;
        readEdid(p,id);
        if(p.name.empty())p.name=adapter.DeviceName;
        if(mode.dmDisplayOrientation==DMDO_90 || mode.dmDisplayOrientation==DMDO_270)std::swap(p.mmWidth,p.mmHeight);
        p.width=LONG(mode.dmPelsWidth);p.height=LONG(mode.dmPelsHeight);
        if(!p.width || !p.height)return false;
        p.estimated=p.mmHeight<=0;
        if(p.estimated){p.mmWidth=p.width*25.4/96;p.mmHeight=p.height*25.4/96;}
        p.px={mode.dmPosition.x,mode.dmPosition.y,mode.dmPosition.x+p.width,mode.dmPosition.y+p.height};
        p.key=fingerprint((id.empty()?std::wstring(adapter.DeviceName):id)+L"|"+std::to_wstring(mode.dmDisplayOrientation));
        panels.push_back(p);
    }
    if(panels.empty())return false;
    std::sort(panels.begin(),panels.end(),[](const Panel& a,const Panel& b){return a.px.left<b.px.left;});
    std::wstring ids;
    double pitch=panels[0].mmHeight/panels[0].height;
    for(const auto& p:panels){
        ids+=p.key+L"-";
        if(!p.px.left && !p.px.top)pitch=p.mmHeight/p.height;
    }
    profile=L"Layout-"+fingerprint(ids);
    bridgeEdges=number(configPath,profile.c_str(),L"BridgeEdges",0,0,1)==1;
    for(auto& p:panels){
        double def=p.px.top*pitch;
        if(!p.model.empty() && std::count_if(panels.begin(),panels.end(),[&](const Panel& q){return q.model==p.model;})==1)
            def=number(appDir+L"QuietMouse.ini",L"TopMm",p.model,def,-10000,10000);
        p.top=number(configPath,profile.c_str(),p.key,def,-10000,10000);
        p.detectedHeight=p.mmHeight;
        double measured=number(configPath,profile.c_str(),p.key+L".height",p.mmHeight,1,10000);
        p.mmWidth*=measured/p.mmHeight;p.mmHeight=measured;
    }
    rowReady=count()>1;
    for(int i=1;i<count();++i){
        const RECT a=panels[i-1].px,b=panels[i].px;
        if(a.right!=b.left || a.bottom<=b.top || b.bottom<=a.top)rowReady=false;
    }
    return true;
}
static int writeLayout(){
    if(!loadLayout())return 2;
    std::wstring result=profile+(rowReady?L"\trow":L"\tpaused")+(bridgeEdges?L"\t1\n":L"\t0\n");
    for(auto& p:panels){
        for(auto& c:p.name)if(c<32)c=L' ';
        result+=p.key+L"\t"+p.name+L"\t"+std::to_wstring(p.mmWidth)+L"\t"+std::to_wstring(p.mmHeight)+L"\t"+std::to_wstring(p.top)+(p.estimated?L"\testimate\t":L"\tedid\t")+std::to_wstring(p.detectedHeight)+L"\n";
    }
    int n=WideCharToMultiByte(CP_UTF8,0,result.data(),int(result.size()),nullptr,0,nullptr,nullptr);
    std::string utf8(n,'\0');
    WideCharToMultiByte(CP_UTF8,0,result.data(),int(result.size()),utf8.data(),n,nullptr,nullptr);
    DWORD written;return WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),utf8.data(),n,&written,nullptr) && written==DWORD(n)?0:4;
}
