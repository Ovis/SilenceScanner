using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SilenceScanner;

class Program
{
    // ==== UI行配置 ====
    private static bool _frameDrawn;
    private static int _prevShownLines;

    private const int RowProgress = 0;
    private const int RowCurrent = 1;
    private const int RowFoundTitle = 3;
    private const int RowFoundStart = 4;

    static void Main(string[] args)
    {
        // バージョン表示
        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var versionString = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
            Console.WriteLine($"SilenceScanner version {versionString}");
            Console.WriteLine("Copyright (c) 2025 Ovis");
            Console.WriteLine("Licensed under the MIT License");
            return;
        }

        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "usage: ./SilenceScanner -- <folder> [--silence 2.0] [--thresh -60] [--hpf 70] [--edgeeps 0.02] [--genre \"Soundtrack\"] [--out result.tsv] [--showmax 10]");
            Console.Error.WriteLine("       ./SilenceScanner --version  # Show version information");
            return;
        }

        Console.OutputEncoding = Encoding.UTF8;

        // Check if required tools are available
        if (!IsCommandAvailable("ffprobe"))
        {
            Console.Error.WriteLine("ERROR: ffprobe not found. Please install ffmpeg toolkit.");
            return;
        }
        if (!IsCommandAvailable("ffmpeg"))
        {
            Console.Error.WriteLine("ERROR: ffmpeg not found. Please install ffmpeg toolkit.");
            return;
        }

        var folder = args[0];
        var minSilenceSec = GetOpt(args, "--silence", 2.0);
        var threshDb = GetOpt(args, "--thresh", -60.0);
        var hpfHz = GetOpt(args, "--hpf", 70.0);
        var edgeEps = GetOpt(args, "--edgeeps", 0.02);
        var outPath = GetOpt(args, "--out", "silence_candidates.tsv");
        var genreFilter = GetOptNullable(args, "--genre");
        var showMax = GetOptInt(args, "--showmax", 10);

        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine("folder not found: " + folder);
            return;
        }

        var isTty = !(Console.IsOutputRedirected || Console.IsErrorRedirected);

        // 画面をクリアしてから処理開始
        if (isTty)
        {
            try
            {
                Console.Clear();
            }
            catch (IOException)
            {
                // Console.Clear()が失敗しても処理は続行
            }
        }

        var flacFilesArr = Directory
            .EnumerateFiles(folder, "*.flac", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetDirectoryName(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var total = flacFilesArr.Length;
        if (total == 0)
        {
            Console.WriteLine("no flac files.");
            return;
        }

        // TSVヘッダーを先に出力（都度追記運用）
        try
        {
            File.WriteAllText(outPath, "FilePath\tStartSec\tEndSec\tDurationSec\n", Encoding.UTF8);
        }
        catch (IOException ioEx)
        {
            Console.Error.WriteLine($"ERROR: Failed to create output file {outPath}: {ioEx.Message}");
            return;
        }
        catch (UnauthorizedAccessException uaEx)
        {
            Console.Error.WriteLine($"ERROR: Access denied to output file {outPath}: {uaEx.Message}");
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Unexpected error creating output file {outPath}: {ex.Message}");
            return;
        }

        var flaggedSummary = new List<string>();
        var processed = 0;

        foreach (var f in flacFilesArr)
        {
            processed++;
            try
            {
                if (!MatchesGenre(f, genreFilter))
                {
                    RenderUi(processed, total, f, flaggedSummary, isTty, showMax);
                    continue;
                }

                var dur = ProbeDuration(f);
                RenderUi(processed, total, f, flaggedSummary, isTty, showMax);
                if (dur <= 0) continue;

                var segsAll = DetectSilencesAll(f, minSilenceSec, threshDb, hpfHz, dur);
                // 先頭≈0s と 末尾≈duration の無音は除外（長さ不問）
                var segsMid = segsAll.Where(s => !(s.start <= edgeEps || s.end >= dur - edgeEps)).ToList();

                if (segsMid.Count > 0)
                {
                    flaggedSummary.Add($"[FLAG] {f} ({segsMid.Count} segment)");
                    var sb = new StringBuilder();
                    foreach (var s in segsMid)
                    {
                        sb.Append(f).Append('\t')
                          .Append(s.start.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                          .Append(s.end.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                          .Append(s.duration.ToString("F3", CultureInfo.InvariantCulture)).AppendLine();
                    }
                    // 逐次追記
                    try
                    {
                        File.AppendAllText(outPath, sb.ToString(), Encoding.UTF8);
                    }
                    catch (IOException ioEx)
                    {
                        flaggedSummary.Add($"[ERR ] Failed to write to {outPath}: {ioEx.Message}");
                        Console.Error.WriteLine($"ERROR: Failed to write results for {f}: {ioEx.Message}");
                    }
                    catch (UnauthorizedAccessException uaEx)
                    {
                        flaggedSummary.Add($"[ERR ] Access denied to {outPath}: {uaEx.Message}");
                        Console.Error.WriteLine($"ERROR: Access denied writing to {outPath}: {uaEx.Message}");
                    }
                }
            }
            catch (IOException ioEx)
            {
                flaggedSummary.Add($"[ERR ] {f} (IO Error: {ioEx.Message})");
                Console.Error.WriteLine($"ERROR: IO error processing {f}: {ioEx.Message}");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                flaggedSummary.Add($"[ERR ] {f} (Access Denied: {uaEx.Message})");
                Console.Error.WriteLine($"ERROR: Access denied to {f}: {uaEx.Message}");
            }
            catch (ArgumentException argEx)
            {
                flaggedSummary.Add($"[ERR ] {f} (Invalid Path: {argEx.Message})");
                Console.Error.WriteLine($"ERROR: Invalid path {f}: {argEx.Message}");
            }
            catch (Exception ex)
            {
                flaggedSummary.Add($"[ERR ] {f} ({ex.GetType().Name}: {ex.Message})");
                Console.Error.WriteLine($"ERROR: Unexpected error processing {f}: {ex.GetType().Name} - {ex.Message}");
            }

            RenderUi(processed, total, f, flaggedSummary, isTty, showMax);
        }

        FinalUi(processed, total, flaggedSummary, outPath, isTty, showMax);
    }

    // ==== UI ====
    static void EnsureFrame(bool isTty)
    {
        if (_frameDrawn || !isTty) return;
        try
        {
            Console.CursorVisible = false;
        }
        catch (IOException ioEx)
        {
            Console.Error.WriteLine($"WARNING: Failed to set cursor visibility: {ioEx.Message}");
        }
        catch (Exception)
        {
            // Platform may not support cursor visibility, ignore silently
        }

        Console.WriteLine(PadLine("Progress 0/0  0.0%  " + BuildBar(0, 40)));
        Console.WriteLine(PadLine("Current: "));
        Console.WriteLine();
        Console.WriteLine(PadLine("Found: 0 (showing last 0)"));
        _frameDrawn = true;
    }

    static void EnsureBufferHeight(int minRows)
    {
        try
        {
            var bw = Console.BufferWidth > 0 ? Console.BufferWidth : 120;
            var bh = Console.BufferHeight > 0 ? Console.BufferHeight : 300;
            var ww = Console.WindowWidth > 0 ? Console.WindowWidth : 80;
            var wh = Console.WindowHeight > 0 ? Console.WindowHeight : 25;

            var need = Math.Max(minRows, Math.Max(bh, wh));
#pragma warning disable CA1416
            Console.SetBufferSize(Math.Max(bw, ww), need);
#pragma warning restore CA1416
        }
        catch (PlatformNotSupportedException)
        {
            // SetBufferSize is not supported on this platform (Linux/macOS), ignore
        }
        catch (IOException ioEx)
        {
            Console.Error.WriteLine($"WARNING: Failed to set console buffer size: {ioEx.Message}");
        }
        catch (Exception)
        {
            // Other errors handled by downstream clamping
        }
    }

    static void ClearLine(int row)
    {
        var windowHeight = Console.WindowHeight > 0 ? Console.WindowHeight : 25;
        if (row < 0 || row >= windowHeight) return;
        var w = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        Console.SetCursorPosition(0, row);
        Console.Write(new string(' ', w));
    }

    static void RenderUi(int processed, int total, string currentFile, List<string> flagged, bool isTty, int showMax)
    {
        if (!isTty)
        {
            Console.Write($"\r{processed}/{total} {processed * 100.0 / total:0.0}% ");
            return;
        }

        EnsureFrame(isTty);

        // 必要なバッファ行数を確保
        var neededRows = RowFoundStart + Math.Max(1, showMax) + 3;
        EnsureBufferHeight(neededRows);

        // 実描画可能行数にクランプ（ウィンドウサイズに基づく）
        var windowHeight = Console.WindowHeight > 0 ? Console.WindowHeight : 25;
        var maxDrawable = Math.Max(0, windowHeight - RowFoundStart - 2);
        var showMaxEff = Math.Max(0, Math.Min(showMax, maxDrawable));

        var pct = total > 0 ? (double)processed / total : 0;
        var head = $"Progress {processed}/{total}  {pct * 100:0.0}%  {BuildBar(pct, 40)}";

        var w = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        var cur = TruncateMiddleToWidth("Current: ", currentFile, w);

        // 進捗ヘッダ
        Console.SetCursorPosition(0, RowProgress);
        Console.Write(PadLine(head));

        // Current（1行に収めて上書き）
        Console.SetCursorPosition(0, RowCurrent);
        Console.Write(PadLine(cur));
        // 折り返し残骸の消去
        ClearLine(RowCurrent + 1);

        var totalFound = flagged.Count;
        var start = Math.Max(0, totalFound - showMaxEff);
        var showCount = totalFound - start;

        Console.SetCursorPosition(0, RowFoundTitle);
        Console.Write(PadLine($"Found: {totalFound} (showing last {showCount})"));

        // 検出リスト本体
        for (var i = 0; i < showMaxEff; i++)
        {
            var row = RowFoundStart + i;
            if (row >= windowHeight) break;
            Console.SetCursorPosition(0, row);
            if (i < showCount) Console.Write(PadLine(flagged[start + i]));
            else Console.Write(PadLine(""));
        }
        // 前回より少ない表示数なら余り行を消す
        for (var i = showMaxEff; i < _prevShownLines; i++)
        {
            var row = RowFoundStart + i;
            if (row >= windowHeight) break;
            Console.SetCursorPosition(0, row);
            Console.Write(PadLine(""));
        }
        _prevShownLines = showCount;
    }

    static void FinalUi(int processed, int total, List<string> flagged, string outPath, bool isTty, int showMax)
    {
        if (!isTty)
        {
            Console.WriteLine();
            Console.WriteLine($"Output: {outPath}  |  Flagged files: {flagged.Count}");
            return;
        }

        RenderUi(processed, total, "", flagged, isTty, showMax);

        var windowHeight = Console.WindowHeight > 0 ? Console.WindowHeight : 25;
        var maxDrawable = Math.Max(0, windowHeight - RowFoundStart - 2);
        var showMaxEff = Math.Max(0, Math.Min(showMax, maxDrawable));
        var row = RowFoundStart + Math.Max(_prevShownLines, Math.Min(flagged.Count, showMaxEff)) + 1;

        if (row >= windowHeight) row = windowHeight - 1;
        if (row < 0) row = 0;

        Console.SetCursorPosition(0, row);
        Console.Write(PadLine($"Output: {outPath}  |  Flagged files: {flagged.Count}"));
        try
        {
            Console.CursorVisible = true;
        }
        catch (IOException ioEx)
        {
            Console.Error.WriteLine($"WARNING: Failed to restore cursor visibility: {ioEx.Message}");
        }
        catch (Exception)
        {
            // Platform may not support cursor visibility, ignore silently
        }
    }

    static string PadLine(string s)
    {
        var w = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        if (s.Length < w) return s + new string(' ', w - s.Length);
        return s.Length > w ? s.Substring(0, Math.Max(0, w - 1)) : s;
    }

    static string TruncateMiddleToWidth(string prefix, string path, int totalWidth)
    {
        var avail = Math.Max(0, totalWidth - prefix.Length);
        var body = TruncateMiddle(path, avail);
        return prefix + body;
    }

    static string BuildBar(double ratio, int width)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        var filled = (int)Math.Round(width * ratio);
        return "[" + new string('#', filled) + new string('-', Math.Max(0, width - filled)) + "]";
    }

    static string TruncateMiddle(string s, int max)
    {
        if (max <= 8 || s.Length <= max) return s;
        var keep = (max - 3) / 2;
        return s[..keep] + "..." + s[^keep..];
    }

    // ==== ffprobe / ffmpeg ====
    static bool MatchesGenre(string path, string? genreFilter)
    {
        if (string.IsNullOrWhiteSpace(genreFilter)) return true;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("-show_format");
            psi.ArgumentList.Add(path);

            using var p = Process.Start(psi);
            if (p == null)
            {
                Console.Error.WriteLine($"ERROR: Failed to start ffprobe process for: {path}");
                return false;
            }

            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"ERROR: ffprobe exited with code {p.ExitCode} for: {path}");
                return false;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("format", out var fmt))
                return false;
            if (!fmt.TryGetProperty("tags", out var tags))
                return false;

            foreach (var prop in tags.EnumerateObject())
            {
                if (string.Equals(prop.Name, "genre", StringComparison.OrdinalIgnoreCase))
                {
                    var val = prop.Value.GetString() ?? "";
                    if (val.IndexOf(genreFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"ERROR: JSON parse error for {path}: {ex.Message}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to execute ffprobe: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Unexpected error in MatchesGenre for {path}: {ex.Message}");
        }
        return false;
    }

    static double ProbeDuration(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                ArgumentList = { "-v", "error", "-show_entries", "format=duration", "-of", "default=nk=1:nw=1", path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null)
            {
                Console.Error.WriteLine($"ERROR: Failed to start ffprobe process for: {path}");
                return 0.0;
            }

            var outTxt = p.StandardOutput.ReadToEnd();
            var errTxt = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"ERROR: ffprobe failed for {path}: {errTxt}");
                return 0.0;
            }

            return double.TryParse(outTxt.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to execute ffprobe: {ex.Message}");
            return 0.0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Unexpected error in ProbeDuration for {path}: {ex.Message}");
            return 0.0;
        }
    }

    static List<(double start, double end, double duration)> DetectSilencesAll(
        string path, double minSilenceSec, double threshDb, double hpfHz, double duration)
    {
        var segs = new List<(double start, double end, double duration)>();

        try
        {
            var filter = $"highpass=f={hpfHz},silencedetect=noise={threshDb}dB:d={minSilenceSec}";

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostats");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-af"); psi.ArgumentList.Add(filter);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var p = Process.Start(psi);
            if (p == null)
            {
                Console.Error.WriteLine($"ERROR: Failed to start ffmpeg process for: {path}");
                return segs;
            }

            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0 && p.ExitCode != 1) // ffmpeg returns 1 for some non-critical issues
            {
                Console.Error.WriteLine($"WARNING: ffmpeg exited with code {p.ExitCode} for: {path}");
            }

            var reStart = new Regex(@"silence_start:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);
            var reEnd = new Regex(@"silence_end:\s*([0-9]+(?:\.[0-9]+)?)\s*\|\s*silence_duration:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

            double? curStart = null;
            foreach (var line in err.Split('\n'))
            {
                var ms = reStart.Match(line);
                if (ms.Success)
                {
                    if (double.TryParse(ms.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                        curStart = s;
                    continue;
                }
                var me = reEnd.Match(line);
                if (me.Success && curStart.HasValue)
                {
                    if (double.TryParse(me.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var endRel) &&
                        double.TryParse(me.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                    {
                        var startRel = curStart.Value;
                        segs.Add((startRel, endRel, dur));
                        curStart = null;
                    }
                }
            }
            // ファイル終端が無音で閉じた場合の補完
            if (curStart.HasValue && duration > curStart.Value)
            {
                var s = curStart.Value;
                segs.Add((s, duration, duration - s));
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to execute ffmpeg: {ex.Message}");
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to parse ffmpeg output for {path}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Unexpected error in DetectSilencesAll for {path}: {ex.Message}");
        }

        return segs;
    }

    // ==== ツール存在確認 ====
    static bool IsCommandAvailable(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            process.WaitForExit(3000); // 3秒タイムアウト
            return process is { HasExited: true, ExitCode: 0 };
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ==== 引数ヘルパ ====
    static double GetOpt(string[] args, string name, double def)
    {
        var i = Array.IndexOf(args, name);
        if (i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        return def;
    }
    static string GetOpt(string[] args, string name, string def)
    {
        var i = Array.IndexOf(args, name);
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];
        return def;
    }
    static string? GetOptNullable(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];
        return null;
    }
    static int GetOptInt(string[] args, string name, int def)
    {
        var i = Array.IndexOf(args, name);
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v)) return Math.Max(0, v);
        return def;
    }
}
