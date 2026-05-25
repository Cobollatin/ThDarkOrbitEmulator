param(
    [Parameter()]
    [string]$TargetPath = "D:\Games\DarkOrbit Unity",

    [Parameter()]
    [string]$OutputDirectory = (Get-Location).Path,

    [Parameter()]
    [int]$MinimumStringLength = 4,

    [Parameter()]
    [int]$MinimumBase64Length = 20,

    [Parameter()]
    [int]$MaximumBase64Length = 8192,

    [Parameter()]
    [switch]$NoBase64Scan
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HardcodedUrlScanner {
    public sealed class StringSpan {
        public long Offset { get; set; }
        public string Text { get; set; }
    }

    public sealed class UrlMatch {
        public string Kind { get; set; }
        public string StringEncoding { get; set; }
        public string DecodedEncoding { get; set; }
        public long Offset { get; set; }
        public string Url { get; set; }
        public string Context { get; set; }
        public string EncodedCandidate { get; set; }
        public long EncodedCandidateOffset { get; set; }
    }

    public static class NativeStrings {
        private static readonly RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

        public static List<StringSpan> GetAsciiSpans(byte[] bytes, int minimumLength) {
            var spans = new List<StringSpan>();
            int start = -1;
            for (int i = 0; i < bytes.Length; i++) {
                byte b = bytes[i];
                if (b >= 0x20 && b <= 0x7e) {
                    if (start < 0) start = i;
                    continue;
                }

                if (start >= 0) {
                    int length = i - start;
                    if (length >= minimumLength) {
                        spans.Add(new StringSpan {
                            Offset = start,
                            Text = Encoding.ASCII.GetString(bytes, start, length)
                        });
                    }
                    start = -1;
                }
            }

            if (start >= 0) {
                int length = bytes.Length - start;
                if (length >= minimumLength) {
                    spans.Add(new StringSpan {
                        Offset = start,
                        Text = Encoding.ASCII.GetString(bytes, start, length)
                    });
                }
            }

            return spans;
        }

        public static List<StringSpan> GetUtf16LeSpans(byte[] bytes, int minimumLength) {
            var spans = new List<StringSpan>();
            int i = 0;
            while (i < bytes.Length - 1) {
                if (bytes[i] >= 0x20 && bytes[i] <= 0x7e && bytes[i + 1] == 0x00) {
                    int start = i;
                    int chars = 0;
                    while (i < bytes.Length - 1 && bytes[i] >= 0x20 && bytes[i] <= 0x7e && bytes[i + 1] == 0x00) {
                        chars++;
                        i += 2;
                    }

                    if (chars >= minimumLength) {
                        spans.Add(new StringSpan {
                            Offset = start,
                            Text = Encoding.Unicode.GetString(bytes, start, chars * 2)
                        });
                    }
                    continue;
                }

                i++;
            }

            return spans;
        }

        public static List<UrlMatch> ScanBytes(byte[] bytes, int minimumStringLength, int minimumBase64Length, int maximumBase64Length, bool skipBase64) {
            var hits = new List<UrlMatch>();
            var urlRegex = new System.Text.RegularExpressions.Regex(@"(?i)\b(?:https?|wss?|ftp)://(?:localhost(?::\d+)?|direct(?::\d+)?|(?:[a-z0-9-]+\.)+[a-z]{2,})(?:[^\s""'<>\)\]\}\\]*?)(?=(?:https?|wss?|ftp)://|[\s""'<>\)\]\}\\]|$)", Options);
            var base64Regex = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9+/=_-])([A-Za-z0-9+/_-]{" + minimumBase64Length + "," + maximumBase64Length + @"}={0,2})(?![A-Za-z0-9+/=_-])", Options);

            foreach (var span in GetAsciiSpans(bytes, minimumStringLength)) {
                ScanSpan(hits, urlRegex, base64Regex, span.Text, span.Offset, "ascii", 1, skipBase64);
            }

            foreach (var span in GetUtf16LeSpans(bytes, minimumStringLength)) {
                ScanSpan(hits, urlRegex, base64Regex, span.Text, span.Offset, "utf-16le", 2, skipBase64);
            }

            return hits;
        }

        private static void ScanSpan(List<UrlMatch> hits, System.Text.RegularExpressions.Regex urlRegex, System.Text.RegularExpressions.Regex base64Regex, string text, long baseOffset, string stringEncoding, int bytesPerChar, bool skipBase64) {
            foreach (System.Text.RegularExpressions.Match match in urlRegex.Matches(text)) {
                foreach (var segment in SplitAdjacentUrls(match.Value)) {
                    hits.Add(new UrlMatch {
                        Kind = "plain-" + stringEncoding,
                        StringEncoding = stringEncoding,
                        DecodedEncoding = "",
                        Offset = baseOffset + ((match.Index + segment.Start) * bytesPerChar),
                        Url = segment.Value,
                        Context = GetContext(text, match.Index + segment.Start, match.Index + segment.Start + segment.Value.Length),
                        EncodedCandidate = "",
                        EncodedCandidateOffset = -1
                    });
                }
            }

            if (skipBase64) return;

            foreach (System.Text.RegularExpressions.Match candidateMatch in base64Regex.Matches(text)) {
                string candidate = candidateMatch.Groups[1].Value;
                foreach (var decodedBytes in DecodeBase64(candidate)) {
                    ScanDecodedText(hits, urlRegex, decodedBytes, "utf-8", baseOffset, stringEncoding, bytesPerChar, candidateMatch.Groups[1].Index, candidate);
                    ScanDecodedText(hits, urlRegex, decodedBytes, "utf-16le", baseOffset, stringEncoding, bytesPerChar, candidateMatch.Groups[1].Index, candidate);
                }
            }
        }

        private static void ScanDecodedText(List<UrlMatch> hits, System.Text.RegularExpressions.Regex urlRegex, byte[] decodedBytes, string decodedEncoding, long baseOffset, string stringEncoding, int bytesPerChar, int candidateIndex, string candidate) {
            string decodedText;
            if (decodedEncoding == "utf-16le") {
                decodedText = Encoding.Unicode.GetString(decodedBytes);
            } else {
                decodedText = Encoding.UTF8.GetString(decodedBytes);
            }

            foreach (System.Text.RegularExpressions.Match match in urlRegex.Matches(decodedText)) {
                long candidateOffset = baseOffset + (candidateIndex * bytesPerChar);
                foreach (var segment in SplitAdjacentUrls(match.Value)) {
                    hits.Add(new UrlMatch {
                        Kind = "base64-" + stringEncoding + "->" + decodedEncoding,
                        StringEncoding = stringEncoding,
                        DecodedEncoding = decodedEncoding,
                        Offset = candidateOffset,
                        Url = segment.Value,
                        Context = GetContext(decodedText, match.Index + segment.Start, match.Index + segment.Start + segment.Value.Length),
                        EncodedCandidate = candidate,
                        EncodedCandidateOffset = candidateOffset
                    });
                }
            }
        }

        private sealed class UrlSegment {
            public int Start { get; set; }
            public string Value { get; set; }
        }

        private static IEnumerable<UrlSegment> SplitAdjacentUrls(string value) {
            var starts = new List<int>();
            starts.Add(0);

            for (int i = 1; i < value.Length; i++) {
                if (StartsWithScheme(value, i)) {
                    starts.Add(i);
                }
            }

            for (int i = 0; i < starts.Count; i++) {
                int start = starts[i];
                int end = (i + 1 < starts.Count) ? starts[i + 1] : value.Length;
                string segment = value.Substring(start, end - start).TrimEnd('.', ',', ';', ':', '!', '"', '\'');
                if (segment.Length > 0) {
                    yield return new UrlSegment { Start = start, Value = segment };
                }
            }
        }

        private static bool StartsWithScheme(string value, int index) {
            return StartsWithOrdinalIgnoreCase(value, index, "http://")
                || StartsWithOrdinalIgnoreCase(value, index, "https://")
                || StartsWithOrdinalIgnoreCase(value, index, "ws://")
                || StartsWithOrdinalIgnoreCase(value, index, "wss://")
                || StartsWithOrdinalIgnoreCase(value, index, "ftp://");
        }

        private static bool StartsWithOrdinalIgnoreCase(string value, int index, string prefix) {
            if (index + prefix.Length > value.Length) return false;
            return string.Compare(value, index, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static IEnumerable<byte[]> DecodeBase64(string value) {
            foreach (var candidate in new [] { value, value.Replace('-', '+').Replace('_', '/') }) {
                string padded = candidate + new string('=', (4 - (candidate.Length % 4)) % 4);
                byte[] decoded;
                try {
                    decoded = Convert.FromBase64String(padded);
                } catch {
                    continue;
                }

                yield return decoded;
            }
        }

        private static string GetContext(string text, int start, int end) {
            int contextStart = Math.Max(0, start - 80);
            int contextEnd = Math.Min(text.Length, end + 80);
            return text.Substring(contextStart, contextEnd - contextStart);
        }
    }
}
"@

function Get-AsciiStringSpans {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$MinimumLength
    )

    [HardcodedUrlScanner.NativeStrings]::GetAsciiSpans($Bytes, $MinimumLength)
}

function Get-Utf16LeStringSpans {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$MinimumLength
    )

    [HardcodedUrlScanner.NativeStrings]::GetUtf16LeSpans($Bytes, $MinimumLength)
}

function ConvertFrom-PossibleBase64 {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    if ($Value.Length -gt 8192) {
        return
    }

    foreach ($candidate in @($Value, (($Value -replace "-", "+") -replace "_", "/"))) {
        try {
            $padded = $candidate + ("=" * ((4 - ($candidate.Length % 4)) % 4))
            [Convert]::FromBase64String($padded)
        }
        catch {
            continue
        }
    }
}

function Get-Context {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [int]$Start,

        [Parameter(Mandatory)]
        [int]$End,

        [Parameter()]
        [int]$Radius = 80
    )

    $contextStart = [Math]::Max(0, $Start - $Radius)
    $contextEnd = [Math]::Min($Text.Length, $End + $Radius)
    return $Text.Substring($contextStart, $contextEnd - $contextStart)
}

function Add-UrlHit {
    param(
        [Parameter()]
        [Collections.Generic.List[object]]$Hits,

        [Parameter()]
        [Collections.Generic.HashSet[string]]$Seen,

        [Parameter(Mandatory)]
        [IO.FileInfo]$File,

        [Parameter(Mandatory)]
        [string]$Kind,

        [Parameter(Mandatory)]
        [string]$StringEncoding,

        [Parameter()]
        [string]$DecodedEncoding = "",

        [Parameter(Mandatory)]
        [int64]$Offset,

        [Parameter(Mandatory)]
        [string]$Url,

        [Parameter()]
        [string]$Context = "",

        [Parameter()]
        [string]$EncodedCandidate = "",

        [Parameter()]
        [int64]$EncodedCandidateOffset = -1
    )

    $cleanUrl = $Url.TrimEnd(".", ",", ";", ":", "!", '"', "'")
    $key = "{0}`0{1}`0{2}`0{3}`0{4}" -f $File.FullName, $Kind, $Offset, $cleanUrl, $EncodedCandidate.Substring(0, [Math]::Min(120, $EncodedCandidate.Length))
    if (-not $Seen.Add($key)) {
        return
    }

    $uri = $null
    $isAbsolute = [Uri]::TryCreate($cleanUrl, [UriKind]::Absolute, [ref]$uri)

    $Hits.Add([pscustomobject]@{
        file                   = $File.FullName
        file_size              = $File.Length
        file_modified_utc      = $File.LastWriteTimeUtc.ToString("o")
        kind                   = $Kind
        string_encoding        = $StringEncoding
        decoded_encoding       = $DecodedEncoding
        offset                 = $Offset
        url                    = $cleanUrl
        scheme                 = $(if ($isAbsolute) { $uri.Scheme } else { "" })
        host                   = $(if ($isAbsolute) { $uri.Host.ToLowerInvariant() } else { "" })
        port                   = $(if ($isAbsolute -and -not $uri.IsDefaultPort) { $uri.Port } else { $null })
        path                   = $(if ($isAbsolute) { $uri.AbsolutePath } else { "" })
        query                  = $(if ($isAbsolute) { $uri.Query } else { "" })
        context                = $Context
        encoded_candidate      = $EncodedCandidate.Substring(0, [Math]::Min(240, $EncodedCandidate.Length))
        encoded_candidate_len  = $(if ($EncodedCandidate) { $EncodedCandidate.Length } else { 0 })
        encoded_candidate_offs = $EncodedCandidateOffset
    }) | Out-Null
}

$resolvedTarget = (Resolve-Path -LiteralPath $TargetPath).Path
$resolvedOutput = if (Test-Path -LiteralPath $OutputDirectory) {
    (Resolve-Path -LiteralPath $OutputDirectory).Path
}
else {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    (Resolve-Path -LiteralPath $OutputDirectory).Path
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonlPath = Join-Path $resolvedOutput "hardcoded-url-scan-$timestamp.jsonl"
$logPath = Join-Path $resolvedOutput "hardcoded-url-scan-$timestamp.log"

$hits = [Collections.Generic.List[object]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$files = Get-ChildItem -LiteralPath $resolvedTarget -Recurse -Force -File
$scanStarted = Get-Date

Write-Host "Scanning $($files.Count) files under $resolvedTarget"

for ($fileIndex = 0; $fileIndex -lt $files.Count; $fileIndex++) {
    $file = $files[$fileIndex]

    try {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
    }
    catch {
        $hits.Add([pscustomobject]@{
            file                   = $file.FullName
            file_size              = $file.Length
            file_modified_utc      = $file.LastWriteTimeUtc.ToString("o")
            kind                   = "read-error"
            string_encoding        = ""
            decoded_encoding       = ""
            offset                 = -1
            url                    = ""
            scheme                 = ""
            host                   = ""
            port                   = $null
            path                   = ""
            query                  = ""
            context                = $_.Exception.Message
            encoded_candidate      = ""
            encoded_candidate_len  = 0
            encoded_candidate_offs = -1
        }) | Out-Null
        continue
    }

    if ($bytes.Length -eq 0) {
        continue
    }

    $nativeHits = [HardcodedUrlScanner.NativeStrings]::ScanBytes(
        $bytes,
        $MinimumStringLength,
        $MinimumBase64Length,
        $MaximumBase64Length,
        [bool]$NoBase64Scan
    )

    foreach ($nativeHit in $nativeHits) {
        Add-UrlHit `
            -Hits $hits `
            -Seen $seen `
            -File $file `
            -Kind $nativeHit.Kind `
            -StringEncoding $nativeHit.StringEncoding `
            -DecodedEncoding $nativeHit.DecodedEncoding `
            -Offset $nativeHit.Offset `
            -Url $nativeHit.Url `
            -Context $nativeHit.Context `
            -EncodedCandidate $nativeHit.EncodedCandidate `
            -EncodedCandidateOffset $nativeHit.EncodedCandidateOffset
    }

    if ((($fileIndex + 1) % 25) -eq 0) {
        Write-Host ("Scanned {0}/{1} files, hits={2}" -f ($fileIndex + 1), $files.Count, $hits.Count)
    }
}

$orderedHits = @($hits | Sort-Object file, offset, url, kind)
$jsonWriter = [IO.StreamWriter]::new($jsonlPath, $false, [Text.UTF8Encoding]::new($false))
try {
    foreach ($hit in $orderedHits) {
        $jsonWriter.WriteLine(($hit | ConvertTo-Json -Compress -Depth 5))
    }
}
finally {
    $jsonWriter.Dispose()
}

$uniqueUrls = @($orderedHits | Where-Object url | Select-Object -ExpandProperty url -Unique | Sort-Object)
$base64Hits = @($orderedHits | Where-Object { $_.kind -like "base64-*" })
$interestingPattern = "(?i)(darkorbit|bigpoint|bpsecure|deltadna)"
$interestingHits = @($orderedHits | Where-Object { $_.url -match $interestingPattern })
$interestingUrls = @($interestingHits | Select-Object -ExpandProperty url -Unique | Sort-Object)
$topHosts = @($orderedHits | Where-Object host | Group-Object host | Sort-Object Count -Descending | Select-Object -First 25)
$topFiles = @($orderedHits | Group-Object file | Sort-Object Count -Descending | Select-Object -First 25)
$scanEnded = Get-Date

$logLines = [Collections.Generic.List[string]]::new()
$logLines.Add("Hardcoded URL scan") | Out-Null
$logLines.Add("==================") | Out-Null
$logLines.Add("Target: $resolvedTarget") | Out-Null
$logLines.Add("Started: $($scanStarted.ToString("o"))") | Out-Null
$logLines.Add("Finished: $($scanEnded.ToString("o"))") | Out-Null
$logLines.Add("Duration: $([Math]::Round(($scanEnded - $scanStarted).TotalSeconds, 2)) seconds") | Out-Null
$logLines.Add("Files scanned: $($files.Count)") | Out-Null
$logLines.Add("Hits: $($orderedHits.Count)") | Out-Null
$logLines.Add("Unique URLs: $($uniqueUrls.Count)") | Out-Null
$logLines.Add("Base64 URL hits: $($base64Hits.Count)") | Out-Null
$logLines.Add("JSONL report: $jsonlPath") | Out-Null
$logLines.Add("") | Out-Null
$logLines.Add("Interesting DarkOrbit/Bigpoint/BPSecure/deltaDNA URLs") | Out-Null
$logLines.Add("-----------------------------------------------") | Out-Null
foreach ($url in $interestingUrls) {
    $logLines.Add($url) | Out-Null
}
$logLines.Add("") | Out-Null
$logLines.Add("Interesting hit locations") | Out-Null
$logLines.Add("-------------------------") | Out-Null
foreach ($hit in $interestingHits) {
    $logLines.Add(("{0}:{1} [{2}] {3}" -f $hit.file, $hit.offset, $hit.kind, $hit.url)) | Out-Null
}
$logLines.Add("") | Out-Null
$logLines.Add("Base64 decoded URL hits") | Out-Null
$logLines.Add("-----------------------") | Out-Null
foreach ($hit in $base64Hits) {
    $logLines.Add(("{0}:{1} [{2}, decoded={3}, encodedLen={4}] {5}" -f $hit.file, $hit.offset, $hit.kind, $hit.decoded_encoding, $hit.encoded_candidate_len, $hit.url)) | Out-Null
}
$logLines.Add("") | Out-Null
$logLines.Add("Top hosts") | Out-Null
$logLines.Add("---------") | Out-Null
foreach ($group in $topHosts) {
    $logLines.Add(("{0,6} {1}" -f $group.Count, $group.Name)) | Out-Null
}
$logLines.Add("") | Out-Null
$logLines.Add("Top files") | Out-Null
$logLines.Add("---------") | Out-Null
foreach ($group in $topFiles) {
    $logLines.Add(("{0,6} {1}" -f $group.Count, $group.Name)) | Out-Null
}

[IO.File]::WriteAllLines($logPath, $logLines, [Text.UTF8Encoding]::new($false))

Write-Host "Wrote JSONL report: $jsonlPath"
Write-Host "Wrote summary log: $logPath"

[pscustomobject]@{
    TargetPath       = $resolvedTarget
    FilesScanned     = $files.Count
    Hits             = $orderedHits.Count
    UniqueUrls       = $uniqueUrls.Count
    Base64Hits       = $base64Hits.Count
    JsonlReport      = $jsonlPath
    SummaryLog       = $logPath
}
