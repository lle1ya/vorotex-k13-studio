// v5.3: P/Invoke DLLs (user32, hid, setupapi, kernel32, dwmapi…) are loaded only from System32 – never from the exe folder (DLL planting).
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
