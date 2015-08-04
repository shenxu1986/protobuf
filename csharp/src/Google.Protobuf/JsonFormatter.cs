﻿#region Copyright notice and license
// Protocol Buffers - Google's data interchange format
// Copyright 2015 Google Inc.  All rights reserved.
// https://developers.google.com/protocol-buffers/
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//     * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//     * Neither the name of Google Inc. nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
#endregion

using System;
using System.Collections;
using System.Globalization;
using System.Text;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using System.Linq;

namespace Google.Protobuf
{
    /// <summary>
    /// Reflection-based converter from messages to JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instances of this class are thread-safe, with no mutable state.
    /// </para>
    /// <para>
    /// This is a simple start to get JSON formatting working. As it's reflection-based,
    /// it's not as quick as baking calls into generated messages - but is a simpler implementation.
    /// (This code is generally not heavily optimized.)
    /// </para>
    /// </remarks>
    public sealed class JsonFormatter
    {
        private static JsonFormatter defaultInstance = new JsonFormatter(Settings.Default);

        /// <summary>
        /// Returns a formatter using the default settings.
        /// </summary>
        public static JsonFormatter Default { get { return defaultInstance; } }

        /// <summary>
        /// The JSON representation of the first 160 characters of Unicode.
        /// Empty strings are replaced by the static constructor.
        /// </summary>
        private static readonly string[] CommonRepresentations = {
            // C0 (ASCII and derivatives) control characters
            "\\u0000", "\\u0001", "\\u0002", "\\u0003",  // 0x00
          "\\u0004", "\\u0005", "\\u0006", "\\u0007",
          "\\b",     "\\t",     "\\n",     "\\u000b",
          "\\f",     "\\r",     "\\u000e", "\\u000f",
          "\\u0010", "\\u0011", "\\u0012", "\\u0013",  // 0x10
          "\\u0014", "\\u0015", "\\u0016", "\\u0017",
          "\\u0018", "\\u0019", "\\u001a", "\\u001b",
          "\\u001c", "\\u001d", "\\u001e", "\\u001f",
            // Escaping of " and \ are required by www.json.org string definition.
            // Escaping of < and > are required for HTML security.
            "", "", "\\\"", "", "",        "", "",        "",  // 0x20
          "", "", "",     "", "",        "", "",        "",
          "", "", "",     "", "",        "", "",        "",  // 0x30
          "", "", "",     "", "\\u003c", "", "\\u003e", "",
          "", "", "",     "", "",        "", "",        "",  // 0x40
          "", "", "",     "", "",        "", "",        "",
          "", "", "",     "", "",        "", "",        "",  // 0x50
          "", "", "",     "", "\\\\",    "", "",        "",
          "", "", "",     "", "",        "", "",        "",  // 0x60
          "", "", "",     "", "",        "", "",        "",
          "", "", "",     "", "",        "", "",        "",  // 0x70
          "", "", "",     "", "",        "", "",        "\\u007f",
            // C1 (ISO 8859 and Unicode) extended control characters
            "\\u0080", "\\u0081", "\\u0082", "\\u0083",  // 0x80
          "\\u0084", "\\u0085", "\\u0086", "\\u0087",
          "\\u0088", "\\u0089", "\\u008a", "\\u008b",
          "\\u008c", "\\u008d", "\\u008e", "\\u008f",
          "\\u0090", "\\u0091", "\\u0092", "\\u0093",  // 0x90
          "\\u0094", "\\u0095", "\\u0096", "\\u0097",
          "\\u0098", "\\u0099", "\\u009a", "\\u009b",
          "\\u009c", "\\u009d", "\\u009e", "\\u009f"
        };

        static JsonFormatter()
        {
            for (int i = 0; i < CommonRepresentations.Length; i++)
            {
                if (CommonRepresentations[i] == "")
                {
                    CommonRepresentations[i] = ((char) i).ToString();
                }
            }
        }

        private readonly Settings settings;

        /// <summary>
        /// Creates a new formatted with the given settings.
        /// </summary>
        /// <param name="settings">The settings.</param>
        public JsonFormatter(Settings settings)
        {
            this.settings = settings;
        }

        /// <summary>
        /// Formats the specified message as JSON.
        /// </summary>
        /// <param name="message">The message to format.</param>
        /// <returns>The formatted message.</returns>
        public string Format(IMessage message)
        {
            Preconditions.CheckNotNull(message, "message");
            StringBuilder builder = new StringBuilder();
            if (message.Descriptor.IsWellKnownType)
            {
                WriteWellKnownTypeValue(builder, message.Descriptor, message, false);
            }
            else
            {
                WriteMessage(builder, message);
            }
            return builder.ToString();
        }

        private void WriteMessage(StringBuilder builder, IMessage message)
        {
            if (message == null)
            {
                WriteNull(builder);
                return;
            }
            builder.Append("{ ");
            var fields = message.Descriptor.Fields;
            bool first = true;
            // First non-oneof fields
            foreach (var field in fields.InFieldNumberOrder())
            {
                var accessor = field.Accessor;
                // Oneofs are written later
                // TODO: Change to write out fields in order, interleaving oneofs appropriately (as per binary format)
                if (field.ContainingOneof != null && field.ContainingOneof.Accessor.GetCaseFieldDescriptor(message) != field)
                {
                    continue;
                }
                // Omit default values unless we're asked to format them, or they're oneofs (where the default
                // value is still formatted regardless, because that's how we preserve the oneof case).
                object value = accessor.GetValue(message);
                if (field.ContainingOneof == null && !settings.FormatDefaultValues && IsDefaultValue(accessor, value))
                {
                    continue;
                }
                // Omit awkward (single) values such as unknown enum values
                if (!field.IsRepeated && !field.IsMap && !CanWriteSingleValue(accessor.Descriptor, value))
                {
                    continue;
                }

                // Okay, all tests complete: let's write the field value...
                if (!first)
                {
                    builder.Append(", ");
                }
                WriteString(builder, ToCamelCase(accessor.Descriptor.Name));
                builder.Append(": ");
                WriteValue(builder, accessor, value);
                first = false;
            }            
            builder.Append(first ? "}" : " }");
        }

        // Converted from src/google/protobuf/util/internal/utility.cc ToCamelCase
        internal static string ToCamelCase(string input)
        {
            bool capitalizeNext = false;
            bool wasCap = true;
            bool isCap = false;
            bool firstWord = true;
            StringBuilder result = new StringBuilder(input.Length);

            for (int i = 0; i < input.Length; i++, wasCap = isCap)
            {
                isCap = char.IsUpper(input[i]);
                if (input[i] == '_')
                {
                    capitalizeNext = true;
                    if (result.Length != 0)
                    {
                        firstWord = false;
                    }
                    continue;
                }
                else if (firstWord)
                {
                    // Consider when the current character B is capitalized,
                    // first word ends when:
                    // 1) following a lowercase:   "...aB..."
                    // 2) followed by a lowercase: "...ABc..."
                    if (result.Length != 0 && isCap &&
                        (!wasCap || (i + 1 < input.Length && char.IsLower(input[i + 1]))))
                    {
                        firstWord = false;
                    }
                    else
                    {
                        result.Append(char.ToLowerInvariant(input[i]));
                        continue;
                    }
                }
                else if (capitalizeNext)
                {
                    capitalizeNext = false;
                    if (char.IsLower(input[i]))
                    {
                        result.Append(char.ToUpperInvariant(input[i]));
                        continue;
                    }
                }
                result.Append(input[i]);
            }
            return result.ToString();
        }
        
        private static void WriteNull(StringBuilder builder)
        {
            builder.Append("null");
        }

        private static bool IsDefaultValue(IFieldAccessor accessor, object value)
        {
            if (accessor.Descriptor.IsMap)
            {
                IDictionary dictionary = (IDictionary) value;
                return dictionary.Count == 0;
            }
            if (accessor.Descriptor.IsRepeated)
            {
                IList list = (IList) value;
                return list.Count == 0;
            }
            switch (accessor.Descriptor.FieldType)
            {
                case FieldType.Bool:
                    return (bool) value == false;
                case FieldType.Bytes:
                    return (ByteString) value == ByteString.Empty;
                case FieldType.String:
                    return (string) value == "";
                case FieldType.Double:
                    return (double) value == 0.0;
                case FieldType.SInt32:
                case FieldType.Int32:
                case FieldType.SFixed32:
                case FieldType.Enum:
                    return (int) value == 0;
                case FieldType.Fixed32:
                case FieldType.UInt32:
                    return (uint) value == 0;
                case FieldType.Fixed64:
                case FieldType.UInt64:
                    return (ulong) value == 0;
                case FieldType.SFixed64:
                case FieldType.Int64:
                case FieldType.SInt64:
                    return (long) value == 0;
                case FieldType.Float:
                    return (float) value == 0f;
                case FieldType.Message:
                case FieldType.Group: // Never expect to get this, but...
                    return value == null;
                default:
                    throw new ArgumentException("Invalid field type");
            }
        }

        private void WriteValue(StringBuilder builder, IFieldAccessor accessor, object value)
        {
            if (accessor.Descriptor.IsMap)
            {
                WriteDictionary(builder, accessor, (IDictionary) value);
            }
            else if (accessor.Descriptor.IsRepeated)
            {
                WriteList(builder, accessor, (IList) value);
            }
            else
            {
                WriteSingleValue(builder, accessor.Descriptor, value);
            }
        }

        private void WriteSingleValue(StringBuilder builder, FieldDescriptor descriptor, object value)
        {
            switch (descriptor.FieldType)
            {
                case FieldType.Bool:
                    builder.Append((bool) value ? "true" : "false");
                    break;
                case FieldType.Bytes:
                    // Nothing in Base64 needs escaping
                    builder.Append('"');
                    builder.Append(((ByteString) value).ToBase64());
                    builder.Append('"');
                    break;
                case FieldType.String:
                    WriteString(builder, (string) value);
                    break;
                case FieldType.Fixed32:
                case FieldType.UInt32:
                case FieldType.SInt32:
                case FieldType.Int32:
                case FieldType.SFixed32:
                    {
                        IFormattable formattable = (IFormattable) value;
                        builder.Append(formattable.ToString("d", CultureInfo.InvariantCulture));
                        break;
                    }
                case FieldType.Enum:
                    EnumValueDescriptor enumValue = descriptor.EnumType.FindValueByNumber((int) value);
                    // We will already have validated that this is a known value.
                    WriteString(builder, enumValue.Name);
                    break;
                case FieldType.Fixed64:
                case FieldType.UInt64:
                case FieldType.SFixed64:
                case FieldType.Int64:
                case FieldType.SInt64:
                    {
                        builder.Append('"');
                        IFormattable formattable = (IFormattable) value;
                        builder.Append(formattable.ToString("d", CultureInfo.InvariantCulture));
                        builder.Append('"');
                        break;
                    }
                case FieldType.Double:
                case FieldType.Float:
                    string text = ((IFormattable) value).ToString("r", CultureInfo.InvariantCulture);
                    if (text == "NaN" || text == "Infinity" || text == "-Infinity")
                    {
                        builder.Append('"');
                        builder.Append(text);
                        builder.Append('"');
                    }
                    else
                    {
                        builder.Append(text);
                    }
                    break;
                case FieldType.Message:
                case FieldType.Group: // Never expect to get this, but...
                    if (descriptor.MessageType.IsWellKnownType)
                    {
                        WriteWellKnownTypeValue(builder, descriptor.MessageType, value, true);
                    }
                    else
                    {
                        WriteMessage(builder, (IMessage) value);
                    }
                    break;
                default:
                    throw new ArgumentException("Invalid field type: " + descriptor.FieldType);
            }
        }

        /// <summary>
        /// Central interception point for well-known type formatting. Any well-known types which
        /// don't need special handling can fall back to WriteMessage. We avoid assuming that the
        /// values are using the embedded well-known types, in order to allow for dynamic messages
        /// in the future.
        /// </summary>
        private void WriteWellKnownTypeValue(StringBuilder builder, MessageDescriptor descriptor, object value, bool inField)
        {
            if (value == null)
            {
                WriteNull(builder);
                return;
            }
            // For wrapper types, the value will be the (possibly boxed) "native" value,
            // so we can write it as if we were unconditionally writing the Value field for the wrapper type.
            if (descriptor.File == Int32Value.Descriptor.File)
            {
                WriteSingleValue(builder, descriptor.FindFieldByNumber(1), value);
                return;
            }
            if (descriptor.FullName == Timestamp.Descriptor.FullName)
            {
                MaybeWrapInString(builder, value, WriteTimestamp, inField);
                return;
            }
            if (descriptor.FullName == Duration.Descriptor.FullName)
            {
                MaybeWrapInString(builder, value, WriteDuration, inField);
                return;
            }
            if (descriptor.FullName == FieldMask.Descriptor.FullName)
            {
                MaybeWrapInString(builder, value, WriteFieldMask, inField);
                return;
            }
            if (descriptor.FullName == Struct.Descriptor.FullName)
            {
                WriteStruct(builder, (IMessage) value);
                return;
            }
            if (descriptor.FullName == ListValue.Descriptor.FullName)
            {
                var fieldAccessor = descriptor.Fields[ListValue.ValuesFieldNumber].Accessor;
                WriteList(builder, fieldAccessor, (IList) fieldAccessor.GetValue(value));
                return;
            }
            if (descriptor.FullName == Value.Descriptor.FullName)
            {
                WriteStructFieldValue(builder, (IMessage) value);
                return;
            }
            WriteMessage(builder, (IMessage) value);
        }

        /// <summary>
        /// Some well-known types end up as string values... so they need wrapping in quotes, but only
        /// when they're being used as fields within another message.
        /// </summary>
        private void MaybeWrapInString(StringBuilder builder, object value, Action<StringBuilder, IMessage> action, bool inField)
        {
            if (inField)
            {
                builder.Append('"');
                action(builder, (IMessage) value);
                builder.Append('"');
            }
            else
            {
                action(builder, (IMessage) value);
            }
        }

        private void WriteTimestamp(StringBuilder builder, IMessage value)
        {
            // TODO: In the common case where this *is* using the built-in Timestamp type, we could
            // avoid all the reflection at this point, by casting to Timestamp. In the interests of
            // avoiding subtle bugs, don't do that until we've implemented DynamicMessage so that we can prove
            // it still works in that case.
            int nanos = (int) value.Descriptor.Fields[Timestamp.NanosFieldNumber].Accessor.GetValue(value);
            long seconds = (long) value.Descriptor.Fields[Timestamp.SecondsFieldNumber].Accessor.GetValue(value);

            // Even if the original message isn't using the built-in classes, we can still build one... and then
            // rely on it being normalized.
            Timestamp normalized = Timestamp.Normalize(seconds, nanos);
            // Use .NET's formatting for the value down to the second, including an opening double quote (as it's a string value)
            DateTime dateTime = normalized.ToDateTime();
            builder.Append(dateTime.ToString("yyyy'-'MM'-'dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
            AppendNanoseconds(builder, Math.Abs(normalized.Nanos));
            builder.Append('Z');
        }

        private void WriteDuration(StringBuilder builder, IMessage value)
        {
            // TODO: Same as for WriteTimestamp
            int nanos = (int) value.Descriptor.Fields[Duration.NanosFieldNumber].Accessor.GetValue(value);
            long seconds = (long) value.Descriptor.Fields[Duration.SecondsFieldNumber].Accessor.GetValue(value);

            // Even if the original message isn't using the built-in classes, we can still build one... and then
            // rely on it being normalized.
            Duration normalized = Duration.Normalize(seconds, nanos);

            // The seconds part will normally provide the minus sign if we need it, but not if it's 0...
            if (normalized.Seconds == 0 && normalized.Nanos < 0)
            {
                builder.Append('-');
            }

            builder.Append(normalized.Seconds.ToString("d", CultureInfo.InvariantCulture));
            AppendNanoseconds(builder, Math.Abs(normalized.Nanos));
            builder.Append('s');
        }

        private void WriteFieldMask(StringBuilder builder, IMessage value)
        {
            IList paths = (IList) value.Descriptor.Fields[FieldMask.PathsFieldNumber].Accessor.GetValue(value);
            AppendEscapedString(builder, string.Join(",", paths.Cast<string>().Select(ToCamelCase)));
        }

        /// <summary>
        /// Appends a number of nanoseconds to a StringBuilder. Either 0 digits are added (in which
        /// case no "." is appended), or 3 6 or 9 digits.
        /// </summary>
        private static void AppendNanoseconds(StringBuilder builder, int nanos)
        {
            if (nanos != 0)
            {
                builder.Append('.');
                // Output to 3, 6 or 9 digits.
                if (nanos % 1000000 == 0)
                {
                    builder.Append((nanos / 1000000).ToString("d", CultureInfo.InvariantCulture));
                }
                else if (nanos % 1000 == 0)
                {
                    builder.Append((nanos / 1000).ToString("d", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(nanos.ToString("d", CultureInfo.InvariantCulture));
                }
            }
        }

        private void WriteStruct(StringBuilder builder, IMessage message)
        {
            builder.Append("{ ");
            IDictionary fields = (IDictionary) message.Descriptor.Fields[Struct.FieldsFieldNumber].Accessor.GetValue(message);
            bool first = true;
            foreach (DictionaryEntry entry in fields)
            {
                string key = (string) entry.Key;
                IMessage value = (IMessage) entry.Value;
                if (string.IsNullOrEmpty(key) || value == null)
                {
                    throw new InvalidOperationException("Struct fields cannot have an empty key or a null value.");
                }

                if (!first)
                {
                    builder.Append(", ");
                }
                WriteString(builder, key);
                builder.Append(": ");
                WriteStructFieldValue(builder, value);
                first = false;
            }
            builder.Append(first ? "}" : " }");
        }

        private void WriteStructFieldValue(StringBuilder builder, IMessage message)
        {
            var specifiedField = message.Descriptor.Oneofs[0].Accessor.GetCaseFieldDescriptor(message);
            if (specifiedField == null)
            {
                throw new InvalidOperationException("Value message must contain a value for the oneof.");
            }

            object value = specifiedField.Accessor.GetValue(message);
            
            switch (specifiedField.FieldNumber)
            {
                case Value.BoolValueFieldNumber:
                case Value.StringValueFieldNumber:
                case Value.NumberValueFieldNumber:
                    WriteSingleValue(builder, specifiedField, value);
                    return;
                case Value.StructValueFieldNumber:
                case Value.ListValueFieldNumber:
                    // Structs and ListValues are nested messages, and already well-known types.
                    var nestedMessage = (IMessage) specifiedField.Accessor.GetValue(message);
                    WriteWellKnownTypeValue(builder, nestedMessage.Descriptor, nestedMessage, true);
                    return;
                case Value.NullValueFieldNumber:
                    WriteNull(builder);
                    return;
                default:
                    throw new InvalidOperationException("Unexpected case in struct field: " + specifiedField.FieldNumber);
            }
        }

        private void WriteList(StringBuilder builder, IFieldAccessor accessor, IList list)
        {
            builder.Append("[ ");
            bool first = true;
            foreach (var value in list)
            {
                if (!CanWriteSingleValue(accessor.Descriptor, value))
                {
                    continue;
                }
                if (!first)
                {
                    builder.Append(", ");
                }
                WriteSingleValue(builder, accessor.Descriptor, value);
                first = false;
            }
            builder.Append(first ? "]" : " ]");
        }

        private void WriteDictionary(StringBuilder builder, IFieldAccessor accessor, IDictionary dictionary)
        {
            builder.Append("{ ");
            bool first = true;
            FieldDescriptor keyType = accessor.Descriptor.MessageType.FindFieldByNumber(1);
            FieldDescriptor valueType = accessor.Descriptor.MessageType.FindFieldByNumber(2);
            // This will box each pair. Could use IDictionaryEnumerator, but that's ugly in terms of disposal.
            foreach (DictionaryEntry pair in dictionary)
            {
                if (!CanWriteSingleValue(valueType, pair.Value))
                {
                    continue;
                }
                if (!first)
                {
                    builder.Append(", ");
                }
                string keyText;
                switch (keyType.FieldType)
                {
                    case FieldType.String:
                        keyText = (string) pair.Key;
                        break;
                    case FieldType.Bool:
                        keyText = (bool) pair.Key ? "true" : "false";
                        break;
                    case FieldType.Fixed32:
                    case FieldType.Fixed64:
                    case FieldType.SFixed32:
                    case FieldType.SFixed64:
                    case FieldType.Int32:
                    case FieldType.Int64:
                    case FieldType.SInt32:
                    case FieldType.SInt64:
                    case FieldType.UInt32:
                    case FieldType.UInt64:
                        keyText = ((IFormattable) pair.Key).ToString("d", CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new ArgumentException("Invalid key type: " + keyType.FieldType);
                }
                WriteString(builder, keyText);
                builder.Append(": ");
                WriteSingleValue(builder, valueType, pair.Value);
                first = false;
            }
            builder.Append(first ? "}" : " }");
        }

        /// <summary>
        /// Returns whether or not a singular value can be represented in JSON.
        /// Currently only relevant for enums, where unknown values can't be represented.
        /// For repeated/map fields, this always returns true.
        /// </summary>
        private bool CanWriteSingleValue(FieldDescriptor descriptor, object value)
        {
            if (descriptor.FieldType == FieldType.Enum)
            {
                EnumValueDescriptor enumValue = descriptor.EnumType.FindValueByNumber((int) value);
                return enumValue != null;
            }
            return true;
        }

        /// <summary>
        /// Writes a string (including leading and trailing double quotes) to a builder, escaping as required.
        /// </summary>
        /// <remarks>
        /// Other than surrogate pair handling, this code is mostly taken from src/google/protobuf/util/internal/json_escaping.cc.
        /// </remarks>
        private void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            AppendEscapedString(builder, text);
            builder.Append('"');
        }

        /// <summary>
        /// Appends the given text to the string builder, escaping as required.
        /// </summary>
        private void AppendEscapedString(StringBuilder builder, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 0xa0)
                {
                    builder.Append(CommonRepresentations[c]);
                    continue;
                }
                if (char.IsHighSurrogate(c))
                {
                    // Encountered first part of a surrogate pair.
                    // Check that we have the whole pair, and encode both parts as hex.
                    i++;
                    if (i == text.Length || !char.IsLowSurrogate(text[i]))
                    {
                        throw new ArgumentException("String contains low surrogate not followed by high surrogate");
                    }
                    HexEncodeUtf16CodeUnit(builder, c);
                    HexEncodeUtf16CodeUnit(builder, text[i]);
                    continue;
                }
                else if (char.IsLowSurrogate(c))
                {
                    throw new ArgumentException("String contains high surrogate not preceded by low surrogate");
                }
                switch ((uint) c)
                {
                    // These are not required by json spec
                    // but used to prevent security bugs in javascript.
                    case 0xfeff:  // Zero width no-break space
                    case 0xfff9:  // Interlinear annotation anchor
                    case 0xfffa:  // Interlinear annotation separator
                    case 0xfffb:  // Interlinear annotation terminator

                    case 0x00ad:  // Soft-hyphen
                    case 0x06dd:  // Arabic end of ayah
                    case 0x070f:  // Syriac abbreviation mark
                    case 0x17b4:  // Khmer vowel inherent Aq
                    case 0x17b5:  // Khmer vowel inherent Aa
                        HexEncodeUtf16CodeUnit(builder, c);
                        break;

                    default:
                        if ((c >= 0x0600 && c <= 0x0603) ||  // Arabic signs
                            (c >= 0x200b && c <= 0x200f) ||  // Zero width etc.
                            (c >= 0x2028 && c <= 0x202e) ||  // Separators etc.
                            (c >= 0x2060 && c <= 0x2064) ||  // Invisible etc.
                            (c >= 0x206a && c <= 0x206f))
                        {
                            HexEncodeUtf16CodeUnit(builder, c);
                        }
                        else
                        {
                            // No handling of surrogates here - that's done earlier
                            builder.Append(c);
                        }
                        break;
                }
            }
        }

        private const string Hex = "0123456789abcdef";
        private static void HexEncodeUtf16CodeUnit(StringBuilder builder, char c)
        {
            uint utf16 = c;
            builder.Append("\\u");
            builder.Append(Hex[(c >> 12) & 0xf]);
            builder.Append(Hex[(c >> 8) & 0xf]);
            builder.Append(Hex[(c >> 4) & 0xf]);
            builder.Append(Hex[(c >> 0) & 0xf]);
        }

        /// <summary>
        /// Settings controlling JSON formatting.
        /// </summary>
        public sealed class Settings
        {
            private static readonly Settings defaultInstance = new Settings(false);

            /// <summary>
            /// Default settings, as used by <see cref="JsonFormatter.Default"/>
            /// </summary>
            public static Settings Default { get { return defaultInstance; } }

            private readonly bool formatDefaultValues;


            /// <summary>
            /// Whether fields whose values are the default for the field type (e.g. 0 for integers)
            /// should be formatted (true) or omitted (false).
            /// </summary>
            public bool FormatDefaultValues { get { return formatDefaultValues; } }

            /// <summary>
            /// Creates a new <see cref="Settings"/> object with the specified formatting of default values.
            /// </summary>
            /// <param name="formatDefaultValues"><c>true</c> if default values (0, empty strings etc) should be formatted; <c>false</c> otherwise.</param>
            public Settings(bool formatDefaultValues)
            {
                this.formatDefaultValues = formatDefaultValues;
            }
        }
    }
}
