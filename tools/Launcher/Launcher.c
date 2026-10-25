// The portable package's top-level Vesper.exe: starts app\Vesper.exe beside it and exits, so the
// folder someone unzips shows one program and a note instead of the app's few hundred files.
// Built as 32-bit x86, which runs on every Windows PC Vesper supports (natively on Intel and AMD,
// emulated on ARM), whichever build of the app sits in app\.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#define PATH_CHARS 32768

static void Fail(const wchar_t *message)
{
    MessageBoxW(NULL, message, L"Vesper", MB_OK | MB_ICONERROR);
}

// A finished update waits in app.new: Vesper unpacks it there, checked, and marks it ready last.
// Swap it in before starting Vesper. The old copy may still be quitting after asking for this,
// so keep trying for a few seconds; if it never lets go, start the current version instead. The
// old folder is renamed aside and Vesper deletes it once it's running.
static void ApplyStagedUpdate(const wchar_t *root)
{
    static wchar_t current[PATH_CHARS], staged[PATH_CHARS], marker[PATH_CHARS], old[PATH_CHARS];
    if (lstrlenW(root) + 48 >= PATH_CHARS) return;
    lstrcpyW(current, root);
    lstrcatW(current, L"app");
    lstrcpyW(staged, root);
    lstrcatW(staged, L"app.new");
    lstrcpyW(marker, staged);
    lstrcatW(marker, L"\\.vesper-ready");
    if (GetFileAttributesW(marker) == INVALID_FILE_ATTRIBUTES) return;
    wsprintfW(old, L"%sapp.old-%lu", root, GetTickCount());

    for (int attempt = 0; attempt < 50; attempt++)
    {
        if (MoveFileExW(current, old, 0))
        {
            if (MoveFileExW(staged, current, 0))
            {
                lstrcpyW(marker, current);
                lstrcatW(marker, L"\\.vesper-ready");
                DeleteFileW(marker);
            }
            else
            {
                MoveFileExW(old, current, 0); // put the working copy back
            }
            return;
        }
        Sleep(200);
    }
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show)
{
    (void)instance;
    (void)previous;
    (void)arguments;

    // Only Windows' own folder for anything this process loads (it needs nothing else).
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);

    static wchar_t folder[PATH_CHARS], app[PATH_CHARS], commandLine[PATH_CHARS + 64];
    DWORD length = GetModuleFileNameW(NULL, folder, PATH_CHARS);
    if (length == 0 || length >= PATH_CHARS)
    {
        Fail(L"Vesper couldn't find its own folder.");
        return 1;
    }
    wchar_t *slash = folder + length;
    while (slash > folder && *slash != L'\\') slash--;
    if (*slash != L'\\')
    {
        Fail(L"Vesper couldn't find its own folder.");
        return 1;
    }
    // Cut off the file name, leaving "...\", then point at "...\app\" and "...\app\Vesper.exe".
    slash[1] = L'\0';
    if (lstrlenW(folder) + 16 >= PATH_CHARS)
    {
        Fail(L"Vesper's folder path is too long.");
        return 1;
    }
    ApplyStagedUpdate(folder);
    lstrcatW(folder, L"app\\");
    lstrcpyW(app, folder);
    lstrcatW(app, L"Vesper.exe");

    if (GetFileAttributesW(app) == INVALID_FILE_ATTRIBUTES)
    {
        Fail(L"The \"app\" folder that belongs beside this file is missing.\n\n"
             L"Extract the whole Vesper folder from the zip (not just Vesper.exe), then run it again.");
        return 1;
    }

    // An explicit program path, so nothing is looked up on the search path.
    commandLine[0] = L'"';
    lstrcpyW(commandLine + 1, app);
    lstrcatW(commandLine, L"\"");

    STARTUPINFOW startup = { sizeof startup };
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = (WORD)show;
    PROCESS_INFORMATION process;
    if (!CreateProcessW(app, commandLine, NULL, NULL, FALSE, 0, NULL, folder, &startup, &process))
    {
        Fail(L"Windows wouldn't start Vesper. If an antivirus or Smart App Control blocked it, allow app\\Vesper.exe and try again.");
        return 1;
    }
    // Let Vesper's window come to the front, as if it had been opened directly.
    AllowSetForegroundWindow(process.dwProcessId);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}
