// Self-test for ShellExt.cpp (built and run by build.ps1). Registers nothing and launches nothing: Launch() only
// records the command line (NITRODROP_SELFTEST). Creates a few temp files, builds a real IShellItemArray for them
// and goes through DllGetClassObject → IClassFactory → IExplorerCommand like Explorer's surrogate would.
#define NITRODROP_SELFTEST
#include "ShellExt.cpp"

#include <shellapi.h>
#include <cstdio>

static int g_failures = 0;

static void Check(bool ok, const char* what)
{
    std::printf("%s  %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++g_failures;
}

static std::vector<std::wstring> Argv(const std::wstring& cmd)
{
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(cmd.c_str(), &argc);
    std::vector<std::wstring> out;
    for (int i = 0; i < argc; ++i) out.emplace_back(argv[i]);
    LocalFree(argv);
    return out;
}

static std::wstring ReadUtf8(const std::wstring& file)
{
    HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return L"<missing>";
    std::string bytes(static_cast<size_t>(GetFileSize(h, nullptr)), '\0');
    DWORD read = 0;
    if (!bytes.empty()) ReadFile(h, &bytes[0], static_cast<DWORD>(bytes.size()), &read, nullptr);
    CloseHandle(h);
    if (bytes.size() >= 3 && static_cast<unsigned char>(bytes[0]) == 0xEF) return L"<bom>";
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data(), static_cast<int>(bytes.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    if (n > 0) MultiByteToWideChar(CP_UTF8, 0, bytes.data(), static_cast<int>(bytes.size()), &w[0], n);
    return w;
}

int wmain()
{
    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) return 2;

    // ---- quoting: round trip through CommandLineToArgvW ----
    const wchar_t* cases[] = {L"C:\\a.txt", L"C:\\Program Files\\NitroDrop\\NitroDrop.exe", L"C:\\", L"C:\\dir with space\\",
                              L"\\\\server\\share\\a b", L"say \"hi\"", L"tail\\\\", L"",
                              L"C:\\Kullan\u0131c\u0131lar\\\u015Eule\\\u00E7i\u011Fdem \u015F\u00FCk\u00FCr.png"};
    bool quoteOk = true;
    for (const wchar_t* c : cases)
    {
        auto argv = Argv(L"x.exe " + nd::QuoteArg(c) + L" next");
        quoteOk &= argv.size() == 3 && argv[1] == c && argv[2] == L"next";
    }
    Check(quoteOk, "QuoteArg round-trips (drive root, trailing backslash, quotes, spaces, Turkish)");

    // ---- temp files and a real shell item array ----
    wchar_t tempBuf[MAX_PATH + 1];
    GetTempPathW(MAX_PATH + 1, tempBuf);
    std::wstring dir = std::wstring(tempBuf) + L"nitrodrop-shellext-selftest-" + std::to_wstring(GetCurrentProcessId());
    CreateDirectoryW(dir.c_str(), nullptr);
    std::vector<std::wstring> files = {dir + L"\\bir iki.txt", dir + L"\\\u00E7i\u011Fdem \u015F\u00FCk\u00FCr.jpg", dir + L"\\klas\u00F6r"};
    for (size_t i = 0; i < 2; ++i)
    {
        HANDLE h = CreateFileW(files[i].c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }
    CreateDirectoryW(files[2].c_str(), nullptr);

    std::vector<PIDLIST_ABSOLUTE> pidls;
    for (const auto& f : files)
    {
        PIDLIST_ABSOLUTE p = nullptr;
        if (SUCCEEDED(SHParseDisplayName(f.c_str(), nullptr, &p, 0, nullptr))) pidls.push_back(p);
    }
    IShellItemArray* items = nullptr;
    HRESULT hr = pidls.size() == files.size()
        ? SHCreateShellItemArrayFromIDLists(static_cast<UINT>(pidls.size()), const_cast<PCIDLIST_ABSOLUTE_ARRAY>(
                                                reinterpret_cast<PCIDLIST_ABSOLUTE*>(pidls.data())), &items)
        : E_FAIL;
    Check(SUCCEEDED(hr) && items, "IShellItemArray for 2 files + 1 folder");

    // ---- COM path: DllGetClassObject → IClassFactory → IExplorerCommand ----
    IClassFactory* factory = nullptr;
    Check(DllGetClassObject(CLSID_NitroDropSend, IID_IClassFactory, reinterpret_cast<void**>(&factory)) == S_OK && factory,
          "DllGetClassObject(CLSID_NitroDropSend)");
    IID other = IID_IUnknown;
    void* none = nullptr;
    Check(DllGetClassObject(other, IID_IClassFactory, &none) == CLASS_E_CLASSNOTAVAILABLE, "unknown CLSID rejected");

    IExplorerCommand* cmd = nullptr;
    if (factory) factory->CreateInstance(nullptr, IID_IExplorerCommand, reinterpret_cast<void**>(&cmd));
    Check(cmd != nullptr, "CreateInstance(IExplorerCommand)");
    Check(DllCanUnloadNow() == S_FALSE, "DllCanUnloadNow = S_FALSE while an object lives");

    if (cmd && items)
    {
        LPWSTR title = nullptr;
        cmd->GetTitle(items, &title);
        Check(title && std::wstring(title) == L"NitroDrop ile g\u00F6nder", "GetTitle = NitroDrop ile gönder");
        CoTaskMemFree(title);

        LPWSTR icon = nullptr;
        cmd->GetIcon(items, &icon);
        std::wstring iconStr = icon ? icon : L"";
        CoTaskMemFree(icon);
        const std::wstring tail = L"\\NitroDrop.exe,0";
        Check(iconStr.size() > tail.size() && iconStr.compare(iconStr.size() - tail.size(), tail.size(), tail) == 0,
              "GetIcon = <module dir>\\NitroDrop.exe,0");

        EXPCMDSTATE state = ECS_HIDDEN;
        cmd->GetState(items, TRUE, &state);
        Check(state == ECS_ENABLED, "GetState(files + folder) = enabled");
        cmd->GetState(nullptr, TRUE, &state);
        Check(state == ECS_HIDDEN, "GetState(no selection) = hidden");

        Check(SUCCEEDED(cmd->Invoke(items, nullptr)), "Invoke");
        auto argv = Argv(nd::g_lastLaunch);
        bool shape = argv.size() == 5 && argv[1] == L"--send" && argv[2] == files[0] && argv[3] == files[1] && argv[4] == files[2];
        const std::wstring exeTail = L"\\NitroDrop.exe";
        shape &= argv.size() > 0 && argv[0].size() > exeTail.size() &&
                 argv[0].compare(argv[0].size() - exeTail.size(), exeTail.size(), exeTail) == 0;
        Check(shape, "Invoke command = <NitroDrop.exe> --send <p1> <p2> <p3>");
    }

    // ---- long selection → --send-list file ----
    std::vector<std::wstring> many;
    for (int i = 0; i < 400; ++i)
        many.push_back(L"C:\\Users\\Emre Can\\Masa\u00FCst\u00FC\\\u00E7ok uzun bir klas\u00F6r ad\u0131\\dosya " + std::to_wstring(i) +
                       L" - " + std::wstring(40, L'x') + L".jpg");
    bool usedList = false;
    std::wstring longCmd = nd::BuildCommandLine(L"C:\\Program Files\\WindowsApps\\NitroDrop.exe", many, &usedList);
    auto largv = Argv(longCmd);
    Check(usedList && longCmd.size() < 1000 && largv.size() == 3 && largv[1] == L"--send-list", "long selection uses --send-list");
    if (largv.size() == 3)
    {
        std::wstring expect;
        for (const auto& p : many) expect += p + L"\r\n";
        std::wstring name = largv[2].substr(largv[2].find_last_of(L'\\') + 1);
        Check(name.rfind(L"NitroDrop-send-", 0) == 0 && largv[2].rfind(tempBuf, 0) == 0, "list file is %TEMP%\\NitroDrop-send-*.txt");
        Check(ReadUtf8(largv[2]) == expect, "list file = UTF-8 (no BOM), one path per line, CRLF");
        DeleteFileW(largv[2].c_str());
    }
    bool shortList = false;
    nd::BuildCommandLine(L"C:\\a\\NitroDrop.exe", std::vector<std::wstring>(1, L"C:\\x"), &shortList);
    Check(!shortList, "short selection stays on the command line");

    if (cmd) cmd->Release();
    if (items) items->Release();
    for (auto p : pidls) CoTaskMemFree(p);
    Check(DllCanUnloadNow() == S_OK, "DllCanUnloadNow = S_OK after release");

    DeleteFileW(files[0].c_str());
    DeleteFileW(files[1].c_str());
    RemoveDirectoryW(files[2].c_str());
    RemoveDirectoryW(dir.c_str());
    CoUninitialize();

    std::printf(g_failures ? "%d FAILED\n" : "all passed\n", g_failures);
    return g_failures ? 1 : 0;
}
