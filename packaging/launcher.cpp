#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR args, int) {
    wchar_t module[32768]; GetModuleFileNameW(nullptr, module, 32768);
    std::wstring root(module); root = root.substr(0, root.find_last_of(L"\\"));
    std::wstring host = root + L"\\app\\SheepCode.exe";
    std::wstring command = L"\"" + host + L"\"";
    if (args && *args) command += L" " + std::wstring(args);
    SetEnvironmentVariableW(L"SHEEPCODE_HOME", root.c_str());
    SetEnvironmentVariableW(L"DOTNET_ROOT_X64", (root + L"\\runtime\\dotnet").c_str());
    wchar_t oldPath[32768]; GetEnvironmentVariableW(L"PATH", oldPath, 32768);
    std::wstring path = root + L"\\app;" + std::wstring(oldPath); SetEnvironmentVariableW(L"PATH", path.c_str());
    STARTUPINFOW start{}; start.cb = sizeof(start); PROCESS_INFORMATION process{};
    if (!CreateProcessW(host.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, root.c_str(), &start, &process)) {
        MessageBoxW(nullptr, L"No se pudo abrir el runtime privado de SheepCode. Vuelve a ejecutar el setup para reparar la instalación.", L"SheepCode", MB_OK | MB_ICONERROR); return 1;
    }
    CloseHandle(process.hThread); CloseHandle(process.hProcess); return 0;
}
