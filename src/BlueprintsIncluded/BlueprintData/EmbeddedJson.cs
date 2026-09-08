using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BlueprintsV2.BlueprintData
{
    /// <summary>
    /// Helpers for embedding structured values inside the per-building <see cref="JObject"/>
    /// that a blueprint stores (see <see cref="BuildingConfig.AdditionalBuildingData"/>).
    ///
    /// <para><see cref="From"/> writes the value as a real nested JSON node instead of
    /// serializing it to a string and stuffing that string into the object - the old
    /// approach produced escaped JSON-inside-JSON and paid for an extra text encode/decode
    /// on every read and write.</para>
    ///
    /// <para><see cref="To{T}"/> reads it back and still accepts the legacy string form,
    /// so blueprints written by older versions keep loading.</para>
    /// </summary>
    public static class EmbeddedJson
    {
        public static JToken From(object? value) => value is null ? JValue.CreateNull() : JToken.FromObject(value);

        public static T? To<T>(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return default;
            if (token.Type == JTokenType.String) //legacy blueprints stored the value as an escaped JSON string
                return JsonConvert.DeserializeObject<T>(token.Value<string>());
            return token.ToObject<T>();
        }
    }
}
