using System.Collections;
using System.Runtime.InteropServices;

namespace Vesper.Linux.Services;

/// <summary>
/// Process-level defences applied before anything else runs. On Linux:
/// <list type="bullet">
/// <item>The process is made non-dumpable: no core dumps, and no other program running as you can
/// attach a debugger (ptrace) or read its memory through /proc. A dump or an attached debugger
/// would see screen contents, keystrokes, passwords and session keys.</item>
/// <item>No core files at all (RLIMIT_CORE = 0), whatever the system's settings.</item>
/// <item>.NET's diagnostics endpoint (a socket in /tmp that lets programs running as you trace,
/// dump or attach to any .NET process) is switched off. It can only be switched off before the
/// runtime starts, so a release build restarts itself once, in place, with it off, and removes
/// the socket the first start made. The runtime's crash dumps, crash reports, perf maps and event
/// traces are switched off the same way, so nothing is written outside Vesper's own folder.</item>
/// </list>
/// Each step is best-effort: if the system refuses one, Vesper still runs and says so in Diagnostics.
/// </summary>
internal static unsafe class Hardening
{
    private const int PrSetDumpable = 4;
    private const int RlimitCore = 4;
    private const string RestartedMarker = "VESPER_HARDENED";

    private static readonly (string Name, string Value)[] RuntimeSettings =
    [
        ("DOTNET_EnableDiagnostics", "0"),
        ("DOTNET_DbgEnableMiniDump", "0"),
        ("DOTNET_EnableCrashReport", "0"),
        ("DOTNET_PerfMapEnabled", "0"),
        ("DOTNET_EnableEventPipe", "0"),
        ("DOTNET_LTTng", "0"),
    ];

    private static readonly List<string> Notes = [];

    public static void Apply(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return;
        // glibc's soname; "libc" alone names a linker script on most distributions.
        if (!NativeLibrary.TryLoad("libc.so.6", out nint libc) && !NativeLibrary.TryLoad("libc", out libc))
        {
            Notes.Add("Couldn't load the C library to harden the process.");
            return;
        }

        // First thing: from here on nothing running as this user can attach to or dump Vesper.
        if (NativeLibrary.TryGetExport(libc, "prctl", out nint prctlExport))
        {
            var prctl = (delegate* unmanaged<int, nuint, nuint, nuint, nuint, int>)prctlExport;
            if (prctl(PrSetDumpable, 0, 0, 0, 0) != 0) Notes.Add($"Couldn't make the process non-dumpable (errno {Marshal.GetLastSystemError()}).");
        }
        if (NativeLibrary.TryGetExport(libc, "setrlimit", out nint setrlimitExport))
        {
            var setrlimit = (delegate* unmanaged<int, RLimit*, int>)setrlimitExport;
            var noCore = new RLimit { Current = 0, Maximum = 0 };
            if (setrlimit(RlimitCore, &noCore) != 0) Notes.Add($"Couldn't turn core dumps off (errno {Marshal.GetLastSystemError()}).");
        }

#if !DEBUG
        // Development builds keep the endpoint, so a debugger can attach.
        if (NativeLibrary.TryGetExport(libc, "execve", out nint execveExport))
        {
            RestartWithoutDiagnostics(args, (delegate* unmanaged<nint, nint*, nint*, int>)execveExport);
        }
#else
        _ = args;
#endif
    }

    /// <summary>What the startup defences couldn't do, for Diagnostics once it's running.</summary>
    public static void Report()
    {
        foreach (string note in Notes) Diagnostics.Add(note);
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics") != "0")
        {
            Diagnostics.Add(".NET's diagnostics endpoint is on for this process (a development build, or not started from Vesper's own executable).");
        }
    }

    private static void RestartWithoutDiagnostics(string[] args, delegate* unmanaged<nint, nint*, nint*, int> execve)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics") == "0") return;
        if (Environment.GetEnvironmentVariable(RestartedMarker) == "1") return; // never loop
        // Only Vesper's own executable can be restarted in place; under "dotnet vesper.dll" the
        // process is the dotnet host.
        if (Environment.ProcessPath is not { } exe || !Path.GetFileName(exe).Equals("vesper", StringComparison.Ordinal)) return;

        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string key && e.Value is string value) environment[key] = value;
        }
        foreach (var (name, value) in RuntimeSettings) environment[name] = value;
        environment[RestartedMarker] = "1";

        var strings = new List<nint>();
        try
        {
            nint path = Utf8(exe);
            var argv = new nint[args.Length + 2]; // null-terminated
            argv[0] = Utf8(exe);
            for (int i = 0; i < args.Length; i++) argv[i + 1] = Utf8(args[i]);
            var envp = new nint[environment.Count + 1];
            int n = 0;
            foreach (var (key, value) in environment) envp[n++] = Utf8($"{key}={value}");

            // The first start's diagnostics socket would outlive the restart: remove it.
            RemoveDiagnosticsSocket();
            fixed (nint* a = argv)
            fixed (nint* v = envp)
            {
                execve(path, a, v);
            }
            // Only reached if exec failed; carry on as we are.
            Notes.Add($"Couldn't restart with .NET diagnostics off (errno {Marshal.GetLastSystemError()}).");
        }
        finally
        {
            foreach (nint p in strings) Marshal.FreeCoTaskMem(p);
        }

        nint Utf8(string s)
        {
            nint p = Marshal.StringToCoTaskMemUTF8(s);
            strings.Add(p);
            return p;
        }
    }

    private static void RemoveDiagnosticsSocket()
    {
        try
        {
            foreach (string socket in Directory.EnumerateFiles(Path.GetTempPath(), $"dotnet-diagnostic-{Environment.ProcessId}-*-socket"))
            {
                File.Delete(socket);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current;
        public ulong Maximum;
    }
}
