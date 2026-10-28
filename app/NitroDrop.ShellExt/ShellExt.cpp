// NitroDrop.ShellExt.dll: File Explorer "Send with NitroDrop" for the MSIX build (Windows 11 modern context menu).
// Title language: the app writes its UI language (two-letter code) to HKCU\Software\Nitrobyte\NitroDrop\UiLanguage on
// every start and language change (Localization/Wpf/UiLanguage.cs); without it the Windows display language is used,
// then English. Read on every GetTitle, so a switch shows on the next right-click.
//
// A tiny IExplorerCommand COM server, native so Explorer never loads a .NET runtime. Registered only through the
// package manifest (packaging/AppxManifest.xml: desktop4:FileExplorerContextMenus + com:SurrogateServer, so it runs in
// a dllhost.exe surrogate with the package identity, not inside explorer.exe). Nothing here writes the registry.
//
// Invoke: collects the selected file-system paths and starts
//     "%LOCALAPPDATA%\Microsoft\WindowsApps\NitroDrop.exe" --send "<p1>" "<p2>" …
// (app execution alias = package identity + survives updates; falls back to NitroDrop.exe next to this DLL). When the
// command line would exceed ~30000 chars (CreateProcess limit 32767) the paths go to a UTF-8 list file in %TEMP%
// (NitroDrop-send-*.txt, one path per line) and the app gets --send-list "<file>" instead. The running NitroDrop
// instance receives it over its single-instance pipe (App.xaml.cs, Services/LaunchArgs.cs) and deletes the file.
//
// Build: app/NitroDrop.ShellExt/build.ps1 (called by packaging/build-release.ps1), static CRT (/MT): the MSIX ships no
// VC++ runtime. SelfTest.cpp includes this file with NITRODROP_SELFTEST and checks it without registering anything.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <shlobj_core.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <new>
#include <string>
#include <vector>

// Must match <com:Class Id> and <desktop5:Verb Clsid> in packaging/AppxManifest.xml.
// {1A8B53AB-8834-43D9-9046-BFD823F0B536}
static const CLSID CLSID_NitroDropSend = {0x1a8b53ab, 0x8834, 0x43d9, {0x90, 0x46, 0xbf, 0xd8, 0x23, 0xf0, 0xb5, 0x36}};

static volatile long g_objects = 0;
static volatile long g_locks = 0;
static HMODULE g_module = nullptr;

namespace nd {

// Same texts as Explorer_MenuVerb in app/AirDrop.App/Localization/Settings*.resx; change both together.
struct TitleEntry
{
    const wchar_t* lang;
    const wchar_t* title;
};
static const TitleEntry kTitles[] = {
    {L"tr", L"NitroDrop ile g\u00F6nder"},
    {L"en", L"Send with NitroDrop"},
    {L"de", L"Mit NitroDrop senden"},
    {L"fr", L"Envoyer avec NitroDrop"},
    {L"ru", L"\u041E\u0442\u043F\u0440\u0430\u0432\u0438\u0442\u044C \u0447\u0435\u0440\u0435\u0437 NitroDrop"},
    {L"pl", L"Wy\u015Blij przez NitroDrop"},
};
static const wchar_t kLanguageKey[] = L"Software\\Nitrobyte\\NitroDrop";   // UiLanguage.RegistryKey
static const wchar_t kLanguageValue[] = L"UiLanguage";                     // UiLanguage.RegistryValue
static const wchar_t kExeName[] = L"NitroDrop.exe";
static const wchar_t kListPrefix[] = L"NitroDrop-send-";   // SendListFile.Prefix
static const size_t kMaxCommandLine = 30000;

// Title for a two-letter code, or nullptr when we have no such language.
const wchar_t* TitleFor(const std::wstring& lang)
{
    for (const auto& e : kTitles)
        if (lang == e.lang) return e.title;
    return nullptr;
}

// Two-letter code for a Windows LANGID (primary language only); empty when unsupported.
std::wstring LangFromLangId(LANGID id)
{
    switch (PRIMARYLANGID(id))
    {
    case LANG_TURKISH: return L"tr";
    case LANG_ENGLISH: return L"en";
    case LANG_GERMAN: return L"de";
    case LANG_FRENCH: return L"fr";
    case LANG_RUSSIAN: return L"ru";
    case LANG_POLISH: return L"pl";
    default: return std::wstring();
    }
}

// The app's UI language from HKCU (empty when the app never ran or the value is unusable).
static std::wstring AppLanguage()
{
    wchar_t buf[16] = {};
    DWORD size = sizeof(buf);
    if (SHRegGetValueW(HKEY_CURRENT_USER, kLanguageKey, kLanguageValue, SRRF_RT_REG_SZ, nullptr, buf, &size) != ERROR_SUCCESS)
        return std::wstring();
    return std::wstring(buf);
}

// App language \u2192 Windows display language \u2192 English.
const wchar_t* Title()
{
    if (const wchar_t* t = TitleFor(AppLanguage())) return t;
    if (const wchar_t* t = TitleFor(LangFromLangId(GetUserDefaultUILanguage()))) return t;
    return kTitles[1].title;
}

// CommandLineToArgvW / CRT rules: quote when needed, double backslashes that precede a quote or the closing quote
// (a selected drive root "C:\" must not become C:"), escape embedded quotes.
std::wstring QuoteArg(const std::wstring& arg)
{
    if (!arg.empty() && arg.find_first_of(L" \t\n\v\"") == std::wstring::npos) return arg;
    std::wstring out = L"\"";
    size_t slashes = 0;
    for (wchar_t c : arg)
    {
        if (c == L'\\') { ++slashes; continue; }
        if (c == L'"') out.append(slashes * 2 + 1, L'\\');
        else out.append(slashes, L'\\');
        out.push_back(c);
        slashes = 0;
    }
    out.append(slashes * 2, L'\\');
    out.push_back(L'"');
    return out;
}

std::wstring ModuleDir()
{
    HMODULE mod = g_module;
    if (!mod)
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&ModuleDir), &mod);
    std::vector<wchar_t> buf(MAX_PATH);
    for (;;)
    {
        DWORD n = GetModuleFileNameW(mod, buf.data(), static_cast<DWORD>(buf.size()));
        if (n == 0) return std::wstring();
        if (n < buf.size()) break;
        if (buf.size() >= 32768) return std::wstring();
        buf.resize(buf.size() * 2);
    }
    std::wstring path(buf.data());
    size_t slash = path.find_last_of(L'\\');
    return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
}

static bool FileExists(const std::wstring& path)
{
    DWORD a = GetFileAttributesW(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

// The app execution alias when present (package identity, stable across updates), else the exe beside this DLL.
std::wstring LauncherPath()
{
    PWSTR local = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &local)))
    {
        std::wstring alias = std::wstring(local) + L"\\Microsoft\\WindowsApps\\" + kExeName;
        CoTaskMemFree(local);
        if (FileExists(alias)) return alias;
    }
    else if (local) CoTaskMemFree(local);
    return ModuleDir() + L"\\" + kExeName;
}

// %TEMP%\NitroDrop-send-<pid>-<tick>-<n>.txt, UTF-8 without BOM, CRLF after every path.
bool WriteListFile(const std::vector<std::wstring>& paths, std::wstring& outPath)
{
    wchar_t temp[MAX_PATH + 1];
    DWORD n = GetTempPathW(MAX_PATH + 1, temp);
    if (n == 0 || n > MAX_PATH) return false;

    std::string utf8;
    for (const auto& p : paths)
    {
        int len = WideCharToMultiByte(CP_UTF8, 0, p.c_str(), static_cast<int>(p.size()), nullptr, 0, nullptr, nullptr);
        if (len <= 0) return false;
        size_t at = utf8.size();
        utf8.resize(at + static_cast<size_t>(len));
        WideCharToMultiByte(CP_UTF8, 0, p.c_str(), static_cast<int>(p.size()), &utf8[at], len, nullptr, nullptr);
        utf8 += "\r\n";
    }

    for (unsigned attempt = 0; attempt < 16; ++attempt)
    {
        std::wstring file = std::wstring(temp) + kListPrefix + std::to_wstring(GetCurrentProcessId()) + L"-" +
                            std::to_wstring(GetTickCount64()) + L"-" + std::to_wstring(attempt) + L".txt";
        HANDLE h = CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
        if (h == INVALID_HANDLE_VALUE)
        {
            if (GetLastError() == ERROR_FILE_EXISTS) continue;
            return false;
        }
        DWORD written = 0;
        BOOL ok = utf8.empty() || WriteFile(h, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        CloseHandle(h);
        if (!ok || written != utf8.size())
        {
            DeleteFileW(file.c_str());
            return false;
        }
        outPath = file;
        return true;
    }
    return false;
}

// Empty result = could not build (list file failed).
std::wstring BuildCommandLine(const std::wstring& exe, const std::vector<std::wstring>& paths, bool* usedList)
{
    if (usedList) *usedList = false;
    std::wstring cmd = QuoteArg(exe) + L" --send";
    for (const auto& p : paths)
    {
        cmd += L' ';
        cmd += QuoteArg(p);
        if (cmd.size() > kMaxCommandLine) break;
    }
    if (cmd.size() <= kMaxCommandLine) return cmd;

    std::wstring list;
    if (!WriteListFile(paths, list)) return std::wstring();
    if (usedList) *usedList = true;
    return QuoteArg(exe) + L" --send-list " + QuoteArg(list);
}

HRESULT PathsFromArray(IShellItemArray* items, std::vector<std::wstring>& paths)
{
    DWORD count = 0;
    HRESULT hr = items->GetCount(&count);
    if (FAILED(hr)) return hr;
    for (DWORD i = 0; i < count; ++i)
    {
        IShellItem* item = nullptr;
        if (FAILED(items->GetItemAt(i, &item))) continue;
        PWSTR path = nullptr;
        if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path)
        {
            paths.emplace_back(path);
            CoTaskMemFree(path);
        }
        item->Release();
    }
    return S_OK;
}

#ifdef NITRODROP_SELFTEST
std::wstring g_lastLaunch;
static HRESULT Launch(std::wstring& cmd)
{
    g_lastLaunch = cmd;
    return S_OK;
}
#else
static HRESULT Launch(std::wstring& cmd)
{
    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};
    // The running instance brings its window forward; let it take the foreground from this invocation.
    AllowSetForegroundWindow(ASFW_ANY);
    if (!CreateProcessW(nullptr, &cmd[0], nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &si, &pi))
        return HRESULT_FROM_WIN32(GetLastError());
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return S_OK;
}
#endif

} // namespace nd

class SendCommand final : public IExplorerCommand
{
public:
    SendCommand() { InterlockedIncrement(&g_objects); }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
        {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&m_refs)); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        long r = InterlockedDecrement(&m_refs);
        if (r == 0) delete this;
        return static_cast<ULONG>(r);
    }

    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return SHStrDupW(nd::Title(), name); }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        std::wstring dir = nd::ModuleDir();
        if (dir.empty()) { *icon = nullptr; return E_FAIL; }
        return SHStrDupW((dir + L"\\" + nd::kExeName + L",0").c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }

    IFACEMETHODIMP GetCanonicalName(GUID* guid) override { *guid = CLSID_NitroDropSend; return S_OK; }

    // Visible only when everything selected is a real file-system item (not "This PC", libraries' virtual nodes…).
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        *state = ECS_HIDDEN;
        if (!items) return S_OK;
        DWORD count = 0;
        if (FAILED(items->GetCount(&count)) || count == 0) return S_OK;
        SFGAOF attrs = 0;
        if (SUCCEEDED(items->GetAttributes(SIATTRIBFLAGS_AND, SFGAO_FILESYSTEM, &attrs)) && (attrs & SFGAO_FILESYSTEM))
            *state = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (!items) return S_OK;
        std::vector<std::wstring> paths;
        HRESULT hr = nd::PathsFromArray(items, paths);
        if (FAILED(hr) || paths.empty()) return hr;
        std::wstring cmd = nd::BuildCommandLine(nd::LauncherPath(), paths, nullptr);
        if (cmd.empty()) return E_FAIL;
        return nd::Launch(cmd);
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_DEFAULT; return S_OK; }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** e) override { *e = nullptr; return E_NOTIMPL; }

private:
    ~SendCommand() { InterlockedDecrement(&g_objects); }
    volatile long m_refs = 1;
};

class SendCommandFactory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    // Static instance: lifetime is the module's (LockServer counts keep it loaded).
    IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }
    IFACEMETHODIMP_(ULONG) Release() override { return 1; }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        SendCommand* cmd = new (std::nothrow) SendCommand();
        if (!cmd) return E_OUTOFMEMORY;
        HRESULT hr = cmd->QueryInterface(riid, ppv);
        cmd->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_locks);
        else InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

static SendCommandFactory g_factory;

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_NitroDropSend) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow(void)
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}

#ifndef NITRODROP_SELFTEST
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
#endif
