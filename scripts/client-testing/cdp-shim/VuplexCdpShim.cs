// Stand-in for "Vuplex WebView.vuplex" (the CEF browser host spawned by DarkOrbit.exe).
// Vuplex only adds --remote-debugging-port when the game calls Web.EnableRemoteDebugging(),
// which DarkOrbit does not, so this shim forwards all arguments to the real host
// ("Vuplex WebView.real.vuplex") and appends --remote-debugging-port for the browser process.
// Port comes from DO_CDP_PORT (default 9222). Installed/removed by CaptureCommon.ps1.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

static class VuplexCdpShim
{
    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (char c in a)
        {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
            sb.Append('\\', bs); bs = 0; sb.Append(c);
        }
        sb.Append('\\', bs * 2); sb.Append('"');
        return sb.ToString();
    }

    static int Main(string[] args)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string real = Path.Combine(dir, "Vuplex WebView.real.vuplex");
        var sb = new StringBuilder();
        bool isSub = false;
        foreach (var a in args) { if (a.StartsWith("--type=")) isSub = true; sb.Append(Quote(a)).Append(' '); }
        if (!isSub)
        {
            string port = Environment.GetEnvironmentVariable("DO_CDP_PORT");
            if (string.IsNullOrEmpty(port)) port = "9222";
            sb.Append("--remote-debugging-port=").Append(port).Append(" --remote-allow-origins=*");
        }
        var psi = new ProcessStartInfo(real, sb.ToString().Trim()) { UseShellExecute = false, WorkingDirectory = dir };
        using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
    }
}
