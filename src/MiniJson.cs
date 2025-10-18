using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CustomTextureReplacer
{
    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            return new Parser(json).ParseValue();
        }

        public static string Serialize(object obj)
        {
            var serializer = new Serializer();
            serializer.SerializeValue(obj);
            return serializer.Builder.ToString();
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _index;

            public Parser(string json)
            {
                _json = json;
                _index = 0;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (_index >= _json.Length)
                    return null;

                return _json[_index] switch
                {
                    '"' => ParseString(),
                    '{' => ParseObject(),
                    '[' => ParseArray(),
                    '-' or >= '0' and <= '9' => ParseNumber(),
                    't' => ParseLiteral("true", true),
                    'f' => ParseLiteral("false", false),
                    'n' => ParseLiteral("null", null),
                    _ => null
                };
            }

            private Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                _index++; // skip '{'

                while (true)
                {
                    SkipWhitespace();
                    if (_index >= _json.Length)
                        break;

                    if (_json[_index] == '}')
                    {
                        _index++;
                        break;
                    }

                    var key = ParseString();
                    SkipWhitespace();
                    if (_index < _json.Length && _json[_index] == ':')
                        _index++;

                    var value = ParseValue();
                    if (key != null)
                        table[key] = value;

                    SkipWhitespace();
                    if (_index < _json.Length && _json[_index] == ',')
                        _index++;
                }

                return table;
            }

            private List<object> ParseArray()
            {
                var array = new List<object>();
                _index++; // skip '['

                while (true)
                {
                    SkipWhitespace();
                    if (_index >= _json.Length)
                        break;

                    if (_json[_index] == ']')
                    {
                        _index++;
                        break;
                    }

                    array.Add(ParseValue());
                    SkipWhitespace();
                    if (_index < _json.Length && _json[_index] == ',')
                        _index++;
                }

                return array;
            }

            private string ParseString()
            {
                var builder = new StringBuilder();
                _index++; // skip initial '"'

                while (_index < _json.Length)
                {
                    var c = _json[_index++];
                    if (c == '"')
                        break;

                    if (c == '\\' && _index < _json.Length)
                    {
                        var esc = _json[_index++];
                        switch (esc)
                        {
                            case '"': builder.Append('"'); break;
                            case '\\': builder.Append('\\'); break;
                            case '/': builder.Append('/'); break;
                            case 'b': builder.Append('\b'); break;
                            case 'f': builder.Append('\f'); break;
                            case 'n': builder.Append('\n'); break;
                            case 'r': builder.Append('\r'); break;
                            case 't': builder.Append('\t'); break;
                            case 'u':
                                if (_index + 3 < _json.Length && ushort.TryParse(_json.Substring(_index, 4), System.Globalization.NumberStyles.HexNumber, null, out var codePoint))
                                {
                                    builder.Append((char)codePoint);
                                    _index += 4;
                                }
                                break;
                        }
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }

                return builder.ToString();
            }

            private object ParseNumber()
            {
                var start = _index;
                while (_index < _json.Length && "0123456789+-.eE".IndexOf(_json[_index]) != -1)
                    _index++;

                var number = _json.Substring(start, _index - start);
                if (double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result))
                    return result;

                return 0d;
            }

            private object ParseLiteral(string literal, object value)
            {
                if (_json.IndexOf(literal, _index, StringComparison.Ordinal) == _index)
                {
                    _index += literal.Length;
                    return value;
                }

                return null;
            }

            private void SkipWhitespace()
            {
                while (_index < _json.Length && char.IsWhiteSpace(_json[_index]))
                    _index++;
            }
        }

        private sealed class Serializer
        {
            private readonly StringBuilder _builder = new StringBuilder();

            public StringBuilder Builder => _builder;

            public void SerializeValue(object value)
            {
                switch (value)
                {
                    case null:
                        _builder.Append("null");
                        break;
                    case string s:
                        SerializeString(s);
                        break;
                    case bool b:
                        _builder.Append(b ? "true" : "false");
                        break;
                    case IDictionary dictionary:
                        SerializeObject(dictionary);
                        break;
                    case IEnumerable enumerable when value is not string:
                        SerializeArray(enumerable);
                        break;
                    case char c:
                        SerializeString(c.ToString());
                        break;
                    default:
                        if (value is IConvertible convertible)
                        {
                            _builder.Append(Convert.ToString(convertible, CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            SerializeString(value.ToString());
                        }
                        break;
                }
            }

            private void SerializeObject(IDictionary dictionary)
            {
                var first = true;
                _builder.Append('{');
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!first)
                        _builder.Append(',');
                    first = false;

                    SerializeString(entry.Key?.ToString() ?? string.Empty);
                    _builder.Append(':');
                    SerializeValue(entry.Value);
                }

                _builder.Append('}');
            }

            private void SerializeArray(IEnumerable array)
            {
                var first = true;
                _builder.Append('[');
                foreach (var item in array)
                {
                    if (!first)
                        _builder.Append(',');
                    first = false;
                    SerializeValue(item);
                }
                _builder.Append(']');
            }

            private void SerializeString(string str)
            {
                _builder.Append('\"');
                foreach (var c in str)
                {
                    switch (c)
                    {
                        case '\\':
                            _builder.Append("\\\\");
                            break;
                        case '\"':
                            _builder.Append("\\\"");
                            break;
                        case '\b':
                            _builder.Append("\\b");
                            break;
                        case '\f':
                            _builder.Append("\\f");
                            break;
                        case '\n':
                            _builder.Append("\\n");
                            break;
                        case '\r':
                            _builder.Append("\\r");
                            break;
                        case '\t':
                            _builder.Append("\\t");
                            break;
                        default:
                            _builder.Append(c);
                            break;
                    }
                }
                _builder.Append('\"');
            }
        }
    }
}

