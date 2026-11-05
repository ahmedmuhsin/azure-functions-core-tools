// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    /// <summary>
    /// Reads the text of the string literals in C# source, and leaves out comments and character literals. The tests of the
    /// texts that a Rust user reads use it, so that a text that is added later is checked without anyone listing it.
    /// Regular, verbatim, interpolated and raw strings are read. The text between the braces of an interpolated string is
    /// code and is not part of the text, but a string inside it is read as one more literal of its own.
    /// </summary>
    internal static class CSharpStringLiterals
    {
        public static IReadOnlyList<StringLiteral> Read(string source, int firstLine = 1)
        {
            var reader = new Reader(source, firstLine);
            var literals = new List<StringLiteral>();
            while (!reader.AtEnd)
            {
                reader.ReadCode(literals);
            }

            return literals;
        }

        internal readonly record struct StringLiteral(int Line, string Text);

        private sealed class Reader
        {
            private readonly string _source;
            private int _position;
            private int _line;

            public Reader(string source, int firstLine)
            {
                _source = source;
                _line = firstLine;
            }

            public bool AtEnd => _position >= _source.Length;

            // Reads one piece of code: a comment, a character literal, a string, or a single character.
            public void ReadCode(List<StringLiteral> literals)
            {
                var current = _source[_position];

                if (current == '/' && Peek(1) == '/')
                {
                    SkipLineComment();
                }
                else if (current == '/' && Peek(1) == '*')
                {
                    SkipBlockComment();
                }
                else if (current == '\'')
                {
                    SkipCharacterLiteral();
                }
                else if (TryReadString(literals))
                {
                    return;
                }
                else
                {
                    Advance();
                }
            }

            private char Peek(int offset)
            {
                var index = _position + offset;
                return index < _source.Length ? _source[index] : '\0';
            }

            private void Advance()
            {
                if (_source[_position] == '\n')
                {
                    _line++;
                }

                _position++;
            }

            private void SkipLineComment()
            {
                while (!AtEnd && _source[_position] != '\n')
                {
                    _position++;
                }
            }

            private void SkipBlockComment()
            {
                _position += 2;
                while (!AtEnd && !(_source[_position] == '*' && Peek(1) == '/'))
                {
                    Advance();
                }

                _position = Math.Min(_position + 2, _source.Length);
            }

            // A character literal is one character or one escape, so a quote in it is not the start of a string.
            private void SkipCharacterLiteral()
            {
                _position++;

                // The escape and the character that it names, or the single character.
                _position += !AtEnd && _source[_position] == '\\' ? 2 : 1;

                while (!AtEnd && _source[_position] != '\'' && _source[_position] != '\n')
                {
                    _position++;
                }

                if (!AtEnd && _source[_position] == '\'')
                {
                    _position++;
                }
            }

            // Reads a string that starts at the position, and adds it to the literals. Returns false when there is none.
            private bool TryReadString(List<StringLiteral> literals)
            {
                var start = _position;
                var prefixEnd = start;
                var interpolated = false;
                var verbatim = false;
                while (prefixEnd < _source.Length && (_source[prefixEnd] == '$' || _source[prefixEnd] == '@') && prefixEnd - start < 3)
                {
                    interpolated |= _source[prefixEnd] == '$';
                    verbatim |= _source[prefixEnd] == '@';
                    prefixEnd++;
                }

                if (prefixEnd >= _source.Length || _source[prefixEnd] != '"')
                {
                    return false;
                }

                var line = _line;
                _position = prefixEnd;
                var text = _source.AsSpan(_position, Math.Min(3, _source.Length - _position)).SequenceEqual("\"\"\"")
                    ? ReadRawString()
                    : ReadQuotedString(interpolated, verbatim, literals);
                literals.Add(new StringLiteral(line, text));
                return true;
            }

            // """ ... """, with as many quotes at the end as at the start.
            private string ReadRawString()
            {
                var quotes = 0;
                while (Peek(quotes) == '"')
                {
                    quotes++;
                }

                _position += quotes;
                var closing = new string('"', quotes);
                var end = _source.IndexOf(closing, _position, StringComparison.Ordinal);
                if (end < 0)
                {
                    end = _source.Length;
                }

                var text = _source.Substring(_position, end - _position);
                while (_position < end)
                {
                    Advance();
                }

                _position = Math.Min(end + quotes, _source.Length);
                return text;
            }

            private string ReadQuotedString(bool interpolated, bool verbatim, List<StringLiteral> literals)
            {
                var text = new StringBuilder();
                _position++;
                while (!AtEnd)
                {
                    var current = _source[_position];

                    if (current == '"')
                    {
                        if (verbatim && Peek(1) == '"')
                        {
                            text.Append('"');
                            _position += 2;
                            continue;
                        }

                        _position++;
                        break;
                    }

                    if (current == '\n' && !verbatim)
                    {
                        // A regular string cannot continue on the next line, so this one is not closed.
                        break;
                    }

                    if (current == '\\' && !verbatim)
                    {
                        text.Append(ReadEscape());
                    }
                    else if (interpolated && current == '{' && Peek(1) == '{')
                    {
                        text.Append('{');
                        _position += 2;
                    }
                    else if (interpolated && current == '}' && Peek(1) == '}')
                    {
                        text.Append('}');
                        _position += 2;
                    }
                    else if (interpolated && current == '{')
                    {
                        SkipInterpolationHole(literals);
                    }
                    else
                    {
                        text.Append(current);
                        Advance();
                    }
                }

                return text.ToString();
            }

            private string ReadEscape()
            {
                var escaped = Peek(1);
                _position += 2;
                switch (escaped)
                {
                    case 'n':
                        return "\n";
                    case 'r':
                        return "\r";
                    case 't':
                        return "\t";
                    case '0':
                        return "\0";
                    case 'u':
                        _position += Math.Min(4, _source.Length - _position);
                        return "?";
                    case 'x':
                        _position += Math.Min(2, _source.Length - _position);
                        return "?";
                    default:
                        return escaped.ToString();
                }
            }

            // The code in the braces of an interpolated string. A string in it is one more literal.
            private void SkipInterpolationHole(List<StringLiteral> literals)
            {
                var depth = 0;
                while (!AtEnd)
                {
                    var current = _source[_position];

                    if (current == '{')
                    {
                        depth++;
                        _position++;
                    }
                    else if (current == '}')
                    {
                        depth--;
                        _position++;
                        if (depth == 0)
                        {
                            return;
                        }
                    }
                    else if (current == '\'')
                    {
                        SkipCharacterLiteral();
                    }
                    else if (!TryReadString(literals))
                    {
                        Advance();
                    }
                }
            }
        }
    }
}
