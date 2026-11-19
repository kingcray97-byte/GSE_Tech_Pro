using System.Runtime.Versioning;

// The 32-bit device host is a Windows-only executable that loads x86 J2534/RP1210 vendor DLLs and
// reads the Windows registry/INI files. Marking the assembly Windows-only silences CA1416
// (platform-analyzer) warnings for the registry/FileSystem APIs it uses.
[assembly: SupportedOSPlatform("windows")]
