using System;

namespace GseTechPro.App.Services;

/// <summary>
/// Configuration for spawning the bundled local AI service (Phase 8). All paths are optional;
/// when <see cref="PythonExecutablePath"/> / <see cref="ServiceWorkingDirectory"/> do not resolve to
/// existing files, <see cref="AiServiceLauncher"/> stays in attach-only mode (dev builds, tests).
/// </summary>
public sealed class AiServiceLaunchOptions
{
    /// <summary>Path to <c>python.exe</c> (bundled: <c>{app}\Python\python.exe</c>).</summary>
    public string? PythonExecutablePath { get; set; }

    /// <summary>Working directory for <c>python -m app.main</c> (bundled: <c>{app}\AiService</c>).</summary>
    public string? ServiceWorkingDirectory { get; set; }

    /// <summary>Optional <c>--config</c> path (bundled: <c>{app}\AiService\config\config.yaml</c>).</summary>
    public string? ConfigPath { get; set; }

    /// <summary>Directory for <c>ai-service.log</c>. Defaults to <c>%LOCALAPPDATA%\GseTechPro\logs</c>.</summary>
    public string? LogDirectory { get; set; }

    /// <summary>Whether spawning is enabled. False when the bundled layout is missing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long to wait for <c>/health</c> after spawn. Model warm-up continues afterwards.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Poll interval while waiting for <c>/health</c> after spawn.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Test hook: replaces <see cref="System.Diagnostics.Process.Start(ProcessStartInfo)"/>.
    /// Return <c>null</c> to simulate a failed spawn.
    /// </summary>
    public Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?>? ProcessStarter { get; set; }

    /// <summary>Resolves the default (bundled) options from <see cref="AppContext.BaseDirectory"/> and env overrides.</summary>
    public static AiServiceLaunchOptions ResolveDefault()
    {
        var options = new AiServiceLaunchOptions();

        var pythonEnv = Environment.GetEnvironmentVariable("GSE_AI_PYTHON");
        var serviceDirEnv = Environment.GetEnvironmentVariable("GSE_AI_SERVICE_DIR");
        var configEnv = Environment.GetEnvironmentVariable("GSE_AI_CONFIG");

        if (!string.IsNullOrWhiteSpace(pythonEnv) || !string.IsNullOrWhiteSpace(serviceDirEnv))
        {
            options.PythonExecutablePath = string.IsNullOrWhiteSpace(pythonEnv)
                ? null
                : pythonEnv.Trim();
            options.ServiceWorkingDirectory = string.IsNullOrWhiteSpace(serviceDirEnv)
                ? null
                : serviceDirEnv.Trim();
            options.ConfigPath = string.IsNullOrWhiteSpace(configEnv) ? null : configEnv.Trim();
            options.Enabled = true;
            return options;
        }

        var baseDir = AppContext.BaseDirectory;
        var python = Path.Combine(baseDir, "Python", "python.exe");
        var serviceDir = Path.Combine(baseDir, "AiService");
        var configPath = Path.Combine(serviceDir, "config", "config.yaml");

        options.PythonExecutablePath = python;
        options.ServiceWorkingDirectory = serviceDir;
        options.ConfigPath = configPath;
        options.Enabled = File.Exists(python) && Directory.Exists(serviceDir);
        return options;
    }
}
