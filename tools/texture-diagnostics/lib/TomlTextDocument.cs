using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Fable2Launcher
{
    // A deliberately limited, lossless editor for this project's scalar config.
    // Not a complete TOML parser: multiline values are rejected before any write.
    // C# 5 compatible so the same implementation can be used by PowerShell 5.1.
    public sealed class TomlTextDocument
    {
        public sealed class Assignment
        {
            public string Section;
            public string Key;
            public string Value;
            public int Line;
            public string Prefix;
            public string Suffix;
        }

        private sealed class Table
        {
            public string Name;
            public int Line;
        }

        private readonly List<string> lines;
        private readonly string newline;
        private readonly bool finalNewline;
        private readonly bool bom;
        private readonly List<Table> tables = new List<Table>();
        private readonly List<Assignment> assignments = new List<Assignment>();
        private static readonly Regex AssignmentPattern = new Regex(
            @"^(\s*)((?:[A-Za-z0-9_-]+)|""(?:\\.|[^""\\])*""|'[^']*')(\s*=\s*)(.*)$");

        public TomlTextDocument(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            bom = text.StartsWith("\uFEFF", StringComparison.Ordinal);
            if (bom) text = text.Substring(1);
            newline = text.Contains("\r\n") ? "\r\n" : "\n";
            finalNewline = text.EndsWith("\n", StringComparison.Ordinal) || text.Length == 0;
            lines = Regex.Split(text, "\r\n|\n").ToList();
            if (lines.Count > 0 && lines[lines.Count - 1] == "" && finalNewline)
                lines.RemoveAt(lines.Count - 1);
            Scan();
        }

        public IList<Assignment> Assignments { get { return assignments.AsReadOnly(); } }

        // Quotes and escaped quotes are respected: # inside a string is data.
        // Reject complex multiline input rather than partially editing it.
        private static int CommentIndex(string line)
        {
            char quote = '\0';
            int square = 0, curly = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quote != '\0')
                {
                    if (quote == '"' && ch == '\\') { i++; continue; }
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch == '#')
                {
                    if (square != 0 || curly != 0) throw new FormatException("Multiline TOML collections are not supported by this editor. File left unchanged.");
                    return i;
                }
                if (ch == '"' || ch == '\'')
                {
                    if (i + 2 < line.Length && line[i + 1] == ch && line[i + 2] == ch)
                        throw new FormatException("Multiline TOML strings are not supported by this editor. File left unchanged.");
                    quote = ch;
                }
                else if (ch == '[') square++;
                else if (ch == ']') square--;
                else if (ch == '{') curly++;
                else if (ch == '}') curly--;
            }
            if (quote != '\0' || square != 0 || curly != 0)
                throw new FormatException("Unclosed or multiline TOML value. File left unchanged.");
            return line.Length;
        }

        private void Scan()
        {
            tables.Clear(); assignments.Clear();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            string section = "";
            for (int i = 0; i < lines.Count; i++)
            {
                string raw = lines[i];
                int comment = CommentIndex(raw);
                string code = raw.Substring(0, comment).TrimEnd();
                string trimmed = code.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    if (!trimmed.EndsWith("]", StringComparison.Ordinal))
                        throw new FormatException("Malformed TOML section at line " + (i + 1));
                    bool array = trimmed.StartsWith("[[", StringComparison.Ordinal);
                    string name = array ? "@array:" + i + ":" + trimmed : Unquote(trimmed.Substring(1, trimmed.Length - 2).Trim());
                    if (!names.Add(name)) throw new FormatException("Duplicate TOML section: " + name);
                    tables.Add(new Table { Name = name, Line = i });
                    section = name;
                    continue;
                }
                Match match = AssignmentPattern.Match(code);
                if (!match.Success) continue; // preserve unowned/dotted assignments verbatim
                string key = Unquote(match.Groups[2].Value);
                if (!keys.Add(section + "\0" + key))
                    throw new FormatException("Duplicate TOML key: " + section + "." + key);
                string prefix = match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value;
                string value = match.Groups[4].Value.TrimEnd();
                assignments.Add(new Assignment {
                    Section = section, Key = key, Value = value, Line = i,
                    Prefix = prefix, Suffix = raw.Substring(prefix.Length + value.Length)
                });
            }
        }

        public string GetValue(string section, string key)
        {
            Assignment entry = assignments.FirstOrDefault(x => x.Section == section && x.Key == key);
            return entry == null ? null : entry.Value;
        }

        public void SetValue(string section, string key, string value)
        {
            if (!Regex.IsMatch(key, "^[A-Za-z0-9_-]+$") || value.Contains("\n") || value.Contains("\r"))
                throw new ArgumentException("Only scalar keys and single-line values may be set.");
            if (CommentIndex(value) != value.Length) throw new ArgumentException("Value must not contain an unquoted comment.");
            Assignment entry = assignments.FirstOrDefault(x => x.Section == section && x.Key == key);
            if (entry != null) lines[entry.Line] = entry.Prefix + value + entry.Suffix;
            else
            {
                int start = -1;
                if (section != "")
                {
                    Table table = tables.FirstOrDefault(x => x.Name == section);
                    if (table == null)
                    {
                        if (lines.Count > 0 && !String.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.Add("");
                        lines.Add("[" + section + "]");
                        lines.Add(key + " = " + value);
                        Scan(); return;
                    }
                    start = table.Line;
                }
                Table next = tables.FirstOrDefault(x => x.Line > start);
                int end = next == null ? lines.Count : next.Line;
                while (end > start + 1 && String.IsNullOrWhiteSpace(lines[end - 1])) end--;
                lines.Insert(end, key + " = " + value);
            }
            Scan();
        }

        public override string ToString()
        {
            return (bom ? "\uFEFF" : "") + String.Join(newline, lines) + (finalNewline ? newline : "");
        }

        public static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '\\': result.Append("\\\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    default:
                        if (ch < 32 || ch == 127) result.Append("\\u" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                        else result.Append(ch);
                        break;
                }
            }
            return result.Append('"').ToString();
        }

        public static string Unquote(string value)
        {
            value = value.Trim();
            if (value.Length < 2) return value;
            if (value[0] == '\'' && value[value.Length - 1] == '\'') return value.Substring(1, value.Length - 2);
            if (value[0] != '"' || value[value.Length - 1] != '"') return value;
            var result = new StringBuilder();
            for (int i = 1; i < value.Length - 1; i++)
            {
                char ch = value[i];
                if (ch != '\\') { result.Append(ch); continue; }
                if (++i >= value.Length - 1) throw new FormatException("Incomplete TOML escape.");
                switch (value[i])
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case 'b': result.Append('\b'); break;
                    case 't': result.Append('\t'); break;
                    case 'n': result.Append('\n'); break;
                    case 'f': result.Append('\f'); break;
                    case 'r': result.Append('\r'); break;
                    case 'u': case 'U':
                        int count = value[i] == 'u' ? 4 : 8;
                        if (i + count >= value.Length - 1) throw new FormatException("Incomplete TOML Unicode escape.");
                        int scalar = Int32.Parse(value.Substring(i + 1, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        result.Append(Char.ConvertFromUtf32(scalar)); i += count; break;
                    default: throw new FormatException("Unknown TOML escape.");
                }
            }
            return result.ToString();
        }
    }
}
