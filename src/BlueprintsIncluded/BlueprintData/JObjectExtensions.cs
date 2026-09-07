using Newtonsoft.Json.Linq;

namespace BlueprintsV2.BlueprintData
{
	/// <summary>
	/// Small read helpers over the per-building <see cref="JObject"/> payloads used by
	/// <see cref="DataTransferHelpers"/>. Each method is a 1:1 replacement for the
	/// <c>GetValue</c> / null-check / <c>Value&lt;T&gt;()</c> pattern that would otherwise
	/// be repeated for every stored field.
	/// </summary>
	public static class JObjectExtensions
	{
		/// <summary>
		/// Faithful replacement for
		/// <c>var t = obj.GetValue(key); if (t == null) return; var v = t.Value&lt;T&gt;();</c>.
		/// Returns <see langword="false"/> (and <paramref name="value"/> = <see langword="default"/>)
		/// when the key is absent; otherwise converts the token with <c>token.Value&lt;T&gt;()</c>.
		/// </summary>
		public static bool TryGet<T>(this JObject obj, string key, out T value)
		{
			value = default;
			var token = obj?.GetValue(key);
			if (token == null)
				return false;
			value = token.Value<T>();
			return true;
		}

		/// <summary>
		/// <see cref="EmbeddedJson"/>-aware variant for nested payloads (lists, dictionaries,
		/// POCOs) that also decodes the legacy "escaped JSON string" form.
		/// </summary>
		public static bool TryGetEmbedded<T>(this JObject obj, string key, out T value)
		{
			value = default;
			var token = obj?.GetValue(key);
			if (token == null)
				return false;
			value = EmbeddedJson.To<T>(token);
			return true;
		}

		/// <summary>
		/// Returns the stored value for <paramref name="key"/>, or <paramref name="fallback"/>
		/// when the key is absent.
		/// </summary>
		public static T GetOr<T>(this JObject obj, string key, T fallback = default)
			=> obj.TryGet<T>(key, out var v) ? v : fallback;
	}
}
