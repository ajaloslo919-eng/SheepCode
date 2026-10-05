#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <string>

static bool run(const std::wstring& exe, std::wstring command, const std::wstring& folder, DWORD& code) {
    STARTUPINFOW start{}; start.cb = sizeof(start); PROCESS_INFORMATION process{};
    if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, folder.c_str(), &start, &process)) return false;
    WaitForSingleObject(process.hProcess, INFINITE); GetExitCodeProcess(process.hProcess, &code); CloseHandle(process.hThread); CloseHandle(process.hProcess); return true;
}
int WINAPI wWinMain(HINSTANCE module, HINSTANCE, PWSTR args, int) {
    wchar_t temp[32768]; GetTempPathW(32768, temp); GUID guid; CoCreateGuid(&guid); wchar_t id[40]; StringFromGUID2(guid, id, 40);
    std::wstring root = std::wstring(temp) + L"SheepCodeSetup-" + id;
    if (!CreateDirectoryW(root.c_str(), nullptr)) return 1;
    auto resource = FindResourceW(module, MAKEINTRESOURCEW(101), RT_RCDATA); if (!resource) return 1;
    auto size = SizeofResource(module, resource); auto data = LockResource(LoadResource(module, resource));
    auto file = CreateFileW((root + L"\\bundle.zip").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr); if (file == INVALID_HANDLE_VALUE) return 1;
    DWORD written; bool saved = WriteFile(file, data, size, &written, nullptr) && written == size; CloseHandle(file); if (!saved) return 1;
    // Pass generated paths as environment values, never interpolate them into PowerShell source.
    SetEnvironmentVariableW(L"SHEEPCODE_SETUP_TEMP", root.c_str());
    wchar_t system[32768]; GetSystemDirectoryW(system, 32768); std::wstring shell = std::wstring(system) + L"\\WindowsPowerShell\\v1.0\\powershell.exe";
    DWORD code; std::wstring extract = L"\"" + shell + L"\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='Stop'; Expand-Archive -LiteralPath (Join-Path $env:SHEEPCODE_SETUP_TEMP 'bundle.zip') -DestinationPath (Join-Path $env:SHEEPCODE_SETUP_TEMP 'unpacked')\"";
    if (!run(shell, extract, root, code) || code != 0) { MessageBoxW(nullptr, L"No se pudo extraer el instalador. Comprueba el espacio de la unidad temporal.", L"SheepCode Setup", MB_OK | MB_ICONERROR); return 1; }
    auto folder = root + L"\\unpacked"; auto host = folder + L"\\SheepCode.Setup.exe";
    SetEnvironmentVariableW(L"DOTNET_ROOT_X64", (folder + L"\\runtime\\dotnet").c_str());
    std::wstring launch = L"\"" + host + L"\"";
    if (args && *args) launch += L" " + std::wstring(args);
    if (!run(host, launch, folder, code)) { MessageBoxW(nullptr, L"El asistente de instalación no pudo arrancar.", L"SheepCode Setup", MB_OK | MB_ICONERROR); return 1; }
    // The unique temporary folder remains available with its diagnostics after an installation failure.
    return (int)code;
}
