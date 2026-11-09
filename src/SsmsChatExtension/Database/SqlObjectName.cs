using System;
using System.Text;

namespace SsmsChatExtension.Database
{
    /// <summary>
    /// A one- or two-part SQL Server object name ([schema].[name]), parsed from text that may or may not use brackets and
    /// quoted again safely, so a name can never close its bracket and add SQL of its own.
    /// </summary>
    internal sealed class SqlObjectName
    {
        private const int MaxPartLength = 128;

        private SqlObjectName(string schema, string name)
        {
            Schema = schema;
            Name = name;
        }

        public string Schema { get; }

        public string Name { get; }

        /// <summary>[schema].[name]</summary>
        public string Quoted
        {
            get { return QuoteName(Schema) + "." + QuoteName(Name); }
        }

        public override string ToString()
        {
            return Schema + "." + Name;
        }

        public bool SameAs(SqlObjectName other)
        {
            return other != null
                   && string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
        }

        public static SqlObjectName FromParts(string schema, string name)
        {
            return new SqlObjectName(schema, name);
        }

        /// <summary>QUOTENAME for identifiers: wraps in brackets and doubles any closing bracket.</summary>
        public static string QuoteName(string identifier)
        {
            return "[" + (identifier ?? string.Empty).Replace("]", "]]") + "]";
        }

        /// <summary>Parses "name", "schema.name", "[schema].[name]"; the schema defaults to dbo.</summary>
        public static SqlObjectName Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("An object name is required.");

            var position = 0;
            var trimmed = text.Trim();
            SqlObjectName name;
            if (!TryRead(trimmed, ref position, out name, out var problem) || position != trimmed.Length)
            {
                throw new ArgumentException(problem ?? ("'" + text.Trim() + "' is not a valid object name."));
            }
            return name;
        }

        /// <summary>
        /// Reads a name starting at <paramref name="position"/> and moves it past the name. On failure returns false and
        /// explains why in <paramref name="problem"/>.
        /// </summary>
        public static bool TryRead(string text, ref int position, out SqlObjectName name, out string problem)
        {
            name = null;
            problem = null;

            string first;
            if (!TryReadPart(text, ref position, out first))
            {
                problem = "'" + Preview(text, position) + "' is not a valid object name.";
                return false;
            }

            if (position < text.Length && text[position] == '.')
            {
                position++;
                string second;
                if (!TryReadPart(text, ref position, out second))
                {
                    problem = "'" + Preview(text, 0) + "' is not a valid object name.";
                    return false;
                }
                if (position < text.Length && text[position] == '.')
                {
                    problem = "Use schema.name (two parts) for '" + Preview(text, 0) + "'; the database is given separately.";
                    return false;
                }
                name = new SqlObjectName(first, second);
            }
            else
            {
                name = new SqlObjectName("dbo", first);
            }
            return true;
        }

        private static bool TryReadPart(string text, ref int position, out string part)
        {
            part = null;
            if (position >= text.Length) return false;

            if (text[position] == '[')
            {
                var builder = new StringBuilder();
                var i = position + 1;
                while (i < text.Length)
                {
                    if (text[i] == ']')
                    {
                        if (i + 1 < text.Length && text[i + 1] == ']')
                        {
                            builder.Append(']');
                            i += 2;
                            continue;
                        }
                        break;
                    }
                    if (char.IsControl(text[i])) return false;
                    builder.Append(text[i]);
                    i++;
                }
                if (i >= text.Length || builder.Length == 0 || builder.Length > MaxPartLength) return false;

                part = builder.ToString();
                position = i + 1;
                return true;
            }

            var start = position;
            var end = start;
            if (end < text.Length && (char.IsLetter(text[end]) || text[end] == '_'))
            {
                end++;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '@' || text[end] == '#' || text[end] == '$')) end++;
            }
            if (end == start || end - start > MaxPartLength) return false;

            part = text.Substring(start, end - start);
            position = end;
            return true;
        }

        private static string Preview(string text, int from)
        {
            var preview = text.Substring(Math.Min(from, text.Length)).Trim();
            return preview.Length > 60 ? preview.Substring(0, 60) + "..." : preview;
        }
    }
}
