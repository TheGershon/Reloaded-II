﻿using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace Reloaded.Mod.Loader.Update.Index.Utility;

/// <summary>
/// Contains common serializer settings.
/// </summary>
public static class Serializer
{
    /// <summary>
    /// Contains settings that should be used with serializing JSON.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new NuGetVersionJsonConverter() }
    };

    public class NuGetVersionJsonConverter : JsonConverter<NuGetVersion>
    {
        public override NuGetVersion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            return NuGetVersion.Parse(reader.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, NuGetVersion? version, JsonSerializerOptions options)
        {
            if (version == null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(version.ToString());
        }
    }
}