using System.Text;

namespace AetheriumForge.Core.Discovery;

/// <summary>
/// Minimal parser for Valve's text KeyValues format used by <c>libraryfolders.vdf</c> and <c>appmanifest_*.acf</c>.
/// </summary>
/// <remarks>
/// Supports quoted and unquoted tokens, nested blocks, <c>//</c> line comments, the escapes <c>\\</c>, <c>\"</c>,
/// <c>\n</c> and <c>\t</c>, and ignores platform conditionals such as <c>[$WIN32]</c>.
/// </remarks>
public static class VdfParser
{
    /// <summary>
    /// Parses a KeyValues document.
    /// </summary>
    /// <param name="text">The document text.</param>
    /// <returns>A synthetic root block (empty key) whose children are the document's top-level keys.</returns>
    /// <exception cref="FormatException">The text is not well-formed KeyValues.</exception>
    public static VdfNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var reader = new Reader(text);
        var root = new VdfNode(string.Empty);
        ParseBlock(ref reader, root, isRoot: true);

        return root;
    }

    private static void ParseBlock(ref Reader reader, VdfNode parent, bool isRoot)
    {
        while (true)
        {
            var token = reader.Next();
            switch (token.Kind)
            {
                case TokenKind.End when isRoot:
                    return;
                case TokenKind.End:
                    throw new FormatException("Unexpected end of KeyValues text: missing '}'.");
                case TokenKind.Close when !isRoot:
                    return;
                case TokenKind.Close:
                    throw new FormatException($"Unexpected '}}' at offset {token.Offset}.");
                case TokenKind.Open:
                    throw new FormatException($"Unexpected '{{' without a key at offset {token.Offset}.");
                case TokenKind.Text:
                    ParseEntry(ref reader, parent, token.Text);
                    break;
                default:
                    throw new FormatException($"Unknown token at offset {token.Offset}.");
            }
        }
    }

    private static void ParseEntry(ref Reader reader, VdfNode parent, string key)
    {
        var token = reader.Next();
        switch (token.Kind)
        {
            case TokenKind.Text:
                parent.Add(new VdfNode(key, token.Text));
                break;
            case TokenKind.Open:
                var block = new VdfNode(key);
                parent.Add(block);
                ParseBlock(ref reader, block, isRoot: false);
                break;
            default:
                throw new FormatException($"Key '{key}' has no value at offset {token.Offset}.");
        }
    }

    private enum TokenKind
    {
        Text,
        Open,
        Close,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Offset);

    private ref struct Reader(string text)
    {
        private readonly string _text = text;
        private int _position;

        public Token Next()
        {
            while (true)
            {
                SkipWhitespaceAndComments();
                if (_position >= _text.Length)
                {
                    return new Token(TokenKind.End, string.Empty, _position);
                }

                var start = _position;
                var current = _text[_position];
                if (current == '{')
                {
                    _position++;
                    return new Token(TokenKind.Open, string.Empty, start);
                }

                if (current == '}')
                {
                    _position++;
                    return new Token(TokenKind.Close, string.Empty, start);
                }

                if (current == '[')
                {
                    // Platform conditional such as [$WIN32]: not relevant on Windows-only discovery, skip it.
                    var close = _text.IndexOf(']', _position);
                    if (close < 0)
                    {
                        throw new FormatException($"Unterminated conditional at offset {start}.");
                    }

                    _position = close + 1;
                    continue;
                }

                return current == '"'
                    ? new Token(TokenKind.Text, ReadQuoted(), start)
                    : new Token(TokenKind.Text, ReadUnquoted(), start);
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (_position < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_position]))
                {
                    _position++;
                }
                else if (_text[_position] == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
                {
                    var newline = _text.IndexOf('\n', _position);
                    _position = newline < 0 ? _text.Length : newline + 1;
                }
                else
                {
                    return;
                }
            }
        }

        private string ReadQuoted()
        {
            var start = _position;
            _position++;
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                var current = _text[_position++];
                if (current == '"')
                {
                    return builder.ToString();
                }

                if (current == '\\' && _position < _text.Length)
                {
                    var escaped = _text[_position++];
                    builder.Append(escaped switch
                    {
                        'n' => "\n",
                        't' => "\t",
                        '\\' => "\\",
                        '"' => "\"",
                        _ => string.Concat("\\", escaped.ToString()),
                    });
                    continue;
                }

                builder.Append(current);
            }

            throw new FormatException($"Unterminated quoted string at offset {start}.");
        }

        private string ReadUnquoted()
        {
            var start = _position;
            while (_position < _text.Length)
            {
                var current = _text[_position];
                if (char.IsWhiteSpace(current) || current is '{' or '}' or '"')
                {
                    break;
                }

                _position++;
            }

            return _text[start.._position];
        }
    }
}
