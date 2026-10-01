// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Optical.Images;

/// <summary>
/// Reads cue sheets. The format has no specification worth the name, so the parser accepts what
/// ripping tools actually write: any case, quoted and unquoted file names, several FILE entries, REM
/// lines and the CD-Text fields it has no use for.
/// </summary>
public static class CueParser
{
    private static readonly string[] FileTypes = ["BINARY", "WAVE", "MP3", "AIFF", "MOTOROLA"];

    public static CueSheet ParseFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Older rippers write the system code page; Latin-1 keeps every byte, which is all the file names need.
            text = Encoding.Latin1.GetString(bytes);
        }

        return Parse(text);
    }

    public static CueSheet Parse(string text)
    {
        var state = new ParserState();
        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = Tokenize(line);
            try
            {
                state.Apply(tokens);
            }
            catch (FormatException ex)
            {
                throw Invalid(lineNumber, ex.Message);
            }
        }

        try
        {
            return state.Finish();
        }
        catch (FormatException ex)
        {
            throw Invalid(null, ex.Message);
        }
    }

    internal static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var hasToken = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static BootrixException Invalid(int? line, string message)
    {
        var where = line is null ? "cue sheet" : $"cue sheet line {line}";
        return new BootrixException(ErrorCode.ImageUnreadable, $"{where}: {message}") { Arguments = [$"{where}: {message}"] };
    }

    private sealed class ParserState
    {
        private readonly List<CueFile> _files = [];
        private readonly List<CueTrack> _tracks = [];
        private CueFile? _file;
        private TrackBuilder? _track;
        private string? _catalog;
        private string? _title;
        private string? _performer;

        public void Apply(List<string> tokens)
        {
            switch (tokens[0].ToUpperInvariant())
            {
                case "FILE":
                    AddFile(tokens);
                    break;
                case "TRACK":
                    StartTrack(tokens);
                    break;
                case "INDEX":
                    CurrentTrack("INDEX").Indexes.Add(new CueIndex(Number(tokens, 1, 0, 99), Time(tokens, 2)));
                    break;
                case "PREGAP":
                    CurrentTrack("PREGAP").Pregap = Time(tokens, 1);
                    break;
                case "POSTGAP":
                    CurrentTrack("POSTGAP").Postgap = Time(tokens, 1);
                    break;
                case "FLAGS":
                    CurrentTrack("FLAGS").Flags.AddRange(tokens.Skip(1));
                    break;
                case "TITLE" when tokens.Count > 1:
                    // Before the first TRACK the field describes the disc, afterwards the track.
                    if (_track is null)
                    {
                        _title = tokens[1];
                    }
                    else
                    {
                        _track.Title = tokens[1];
                    }

                    break;
                case "PERFORMER" when tokens.Count > 1:
                    if (_track is null)
                    {
                        _performer = tokens[1];
                    }
                    else
                    {
                        _track.Performer = tokens[1];
                    }

                    break;
                case "CATALOG" when tokens.Count > 1:
                    _catalog = tokens[1];
                    break;
            }
        }

        public CueSheet Finish()
        {
            CloseTrack();
            if (_tracks.Count == 0)
            {
                throw new FormatException("it contains no TRACK");
            }

            if (_tracks.FirstOrDefault(t => t.Start is null) is { } incomplete)
            {
                throw new FormatException($"track {incomplete.Number} has no INDEX 01");
            }

            return new CueSheet(_files, _tracks, _catalog, _title, _performer);
        }

        private void AddFile(List<string> tokens)
        {
            if (tokens.Count < 2)
            {
                throw new FormatException("FILE needs a file name");
            }

            // The type is the last word; everything in between is the name, so unquoted names with spaces still work.
            var last = tokens[^1];
            var hasType = tokens.Count > 2 && FileTypes.Contains(last, StringComparer.OrdinalIgnoreCase);
            var nameTokens = tokens.Skip(1).Take(tokens.Count - (hasType ? 2 : 1));
            _file = new CueFile(string.Join(' ', nameTokens), hasType ? last.ToUpperInvariant() : "BINARY");
            _files.Add(_file);
        }

        private void StartTrack(List<string> tokens)
        {
            if (_file is null)
            {
                throw new FormatException("TRACK before any FILE");
            }

            var number = Number(tokens, 1, 1, 99);
            if (tokens.Count < 3 || !CueTrackModes.TryParse(tokens[2], out var mode))
            {
                throw new FormatException($"unknown track type '{(tokens.Count < 3 ? "" : tokens[2])}'");
            }

            CloseTrack();
            if (_tracks.Any(t => t.Number == number))
            {
                throw new FormatException($"track {number} appears twice");
            }

            _track = new TrackBuilder(number, mode, _file);
        }

        private TrackBuilder CurrentTrack(string command) =>
            _track ?? throw new FormatException($"{command} before any TRACK");

        private void CloseTrack()
        {
            if (_track is not null)
            {
                _tracks.Add(_track.Build());
                _track = null;
            }
        }

        private static int Number(List<string> tokens, int position, int min, int max)
        {
            if (tokens.Count <= position
                || !int.TryParse(tokens[position], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value < min
                || value > max)
            {
                throw new FormatException($"{tokens[0]} needs a number from {min} to {max}");
            }

            return value;
        }

        private static Msf Time(List<string> tokens, int position) =>
            tokens.Count > position && Msf.TryParse(tokens[position], out var time)
                ? time
                : throw new FormatException($"{tokens[0]} needs a time in mm:ss:ff form");
    }

    private sealed class TrackBuilder(int number, CueTrackMode mode, CueFile file)
    {
        public List<CueIndex> Indexes { get; } = [];

        public List<string> Flags { get; } = [];

        public Msf? Pregap { get; set; }

        public Msf? Postgap { get; set; }

        public string? Title { get; set; }

        public string? Performer { get; set; }

        public CueTrack Build() => new(number, mode, file, Indexes, Pregap, Postgap, Flags, Title, Performer);
    }
}
