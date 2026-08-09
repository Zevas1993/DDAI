using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDAI.Core.Mailbox;

public static class MailboxWireJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions(writeIndented: false);

    public static JsonSerializerOptions OptionsIndented { get; } = CreateOptions(writeIndented: true);

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(WireTimestampJsonConverter.Instance);
        return options;
    }
}

public sealed class WireTimestampJsonConverter : JsonConverter<DateTimeOffset>
{
    internal static WireTimestampJsonConverter Instance { get; } = new();

    public static bool IsValid(DateTimeOffset value) => value != DateTimeOffset.MinValue;

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            reader.GetString() is not { } text ||
            !TryParse(text.AsSpan(), out var value))
        {
            throw new JsonException("Timestamp must use YYYY-MM-DDTHH:MM:SS[.fffffff](Z|+HH:MM|-HH:MM) with a valid non-minimum DateTimeOffset value.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        if (!IsValid(value))
        {
            throw new JsonException("The minimum DateTimeOffset value is not a valid wire timestamp.");
        }

        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
        writer.WriteRawValue($"\"{text}\"", skipInputValidation: true);
    }

    private static bool TryParse(ReadOnlySpan<char> text, out DateTimeOffset value)
    {
        value = default;
        if (text.Length < 20 ||
            text[4] != '-' ||
            text[7] != '-' ||
            text[10] != 'T' ||
            text[13] != ':' ||
            text[16] != ':' ||
            !TryReadAsciiNumber(text[..4], out var year) ||
            !TryReadAsciiNumber(text.Slice(5, 2), out var month) ||
            !TryReadAsciiNumber(text.Slice(8, 2), out var day) ||
            !TryReadAsciiNumber(text.Slice(11, 2), out var hour) ||
            !TryReadAsciiNumber(text.Slice(14, 2), out var minute) ||
            !TryReadAsciiNumber(text.Slice(17, 2), out var second))
        {
            return false;
        }

        var index = 19;
        var fractionTicks = 0;
        if (index < text.Length && text[index] == '.')
        {
            index++;
            var fractionStart = index;
            while (index < text.Length && IsAsciiDigit(text[index]))
            {
                index++;
            }

            var fractionLength = index - fractionStart;
            if (fractionLength is < 1 or > 7 ||
                !TryReadAsciiNumber(text.Slice(fractionStart, fractionLength), out fractionTicks))
            {
                return false;
            }

            for (var padding = fractionLength; padding < 7; padding++)
            {
                fractionTicks *= 10;
            }
        }

        TimeSpan offset;
        if (index == text.Length - 1 && text[index] == 'Z')
        {
            offset = TimeSpan.Zero;
        }
        else
        {
            if (text.Length - index != 6 ||
                (text[index] != '+' && text[index] != '-') ||
                text[index + 3] != ':' ||
                !TryReadAsciiNumber(text.Slice(index + 1, 2), out var offsetHour) ||
                !TryReadAsciiNumber(text.Slice(index + 4, 2), out var offsetMinute) ||
                offsetHour > 14 ||
                offsetMinute > 59 ||
                (offsetHour == 14 && offsetMinute != 0))
            {
                return false;
            }

            var offsetTicks = new TimeSpan(offsetHour, offsetMinute, 0);
            offset = text[index] == '-' ? -offsetTicks : offsetTicks;
        }

        try
        {
            var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(fractionTicks);
            value = new DateTimeOffset(local, offset);
            return IsValid(value);
        }
        catch (ArgumentException)
        {
            value = default;
            return false;
        }
    }

    private static bool TryReadAsciiNumber(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!IsAsciiDigit(character))
            {
                value = 0;
                return false;
            }

            value = (value * 10) + (character - '0');
        }

        return true;
    }

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';
}
