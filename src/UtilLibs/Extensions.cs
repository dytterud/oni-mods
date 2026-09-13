using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static STRINGS.DUPLICANTS.STATUSITEMS;

namespace UtilLibs
{
	public static class Extensions
	{
		public static IEnumerable<TSource> DistinctBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
		{
			var known = new HashSet<TKey>();
			return source.Where(element => known.Add(keySelector(element)));
		}


		public static void AddRange(this IDictionary dict, IDictionary other)
		{
			foreach (DictionaryEntry item in other)
			{
				dict.Add(item.Key, item.Value);
			}
		}
		public static T CreateDelegate<T>(this MethodInfo method) where T : MulticastDelegate => (T)Delegate.CreateDelegate(typeof(T), method);




		/// <summary>
		/// Compresses a string.
		/// </summary>
		/// <param name="text">The text.</param>
		/// <returns></returns>
		public static string CompressString(this string text)
		{
			//return "```" + text + "```";
			byte[] buffer = Encoding.UTF8.GetBytes(text);
			byte[] compressedData;
			using (var memoryStream = new MemoryStream())
			{
				using (var gZipStream = new GZipStream(memoryStream, CompressionMode.Compress, true))
				{
					gZipStream.Write(buffer, 0, buffer.Length);
				}

				// ToArray rather than a rewind-and-Read of memoryStream.Length bytes: it cannot
				// come up short, and it says what is meant. (MemoryStream.Read would in fact have
				// returned everything, but the shape invited the bug DecompressString really had.)
				compressedData = memoryStream.ToArray();
			}

			var gZipBuffer = new byte[compressedData.Length + 4];
			Buffer.BlockCopy(compressedData, 0, gZipBuffer, 4, compressedData.Length);
			Buffer.BlockCopy(BitConverter.GetBytes(buffer.Length), 0, gZipBuffer, 0, 4);
			return Convert.ToBase64String(gZipBuffer);
		}

		/// <summary>
		/// Decompresses a string.
		/// </summary>
		/// <param name="compressedText">The compressed text.</param>
		/// <returns></returns>
		public static string DecompressString(this string compressedText)
		{
			try
			{
				byte[] gZipBuffer = Convert.FromBase64String(compressedText);

				// The first four bytes are the uncompressed length. CompressString still writes it,
				// so the format is unchanged and strings produced by other versions still read - but
				// it is no longer trusted here. It arrives from the clipboard, i.e. unvalidated
				// input, and sizing the output buffer from it had two failure modes: a value that
				// disagreed with the real payload silently truncated the result or NUL-padded it
				// (returning a string that looks fine and parses wrong), and a corrupt or hostile
				// value demanded an allocation of that size before anything could sanity-check it.
				// Decompressing into a growable stream needs no length up front and cannot disagree
				// with the data.
				using (var source = new MemoryStream(gZipBuffer, 4, gZipBuffer.Length - 4))
				using (var gZipStream = new GZipStream(source, CompressionMode.Decompress))
				using (var decompressed = new MemoryStream())
				{
					// CopyTo loops until the stream really ends. The single Read this replaces kept
					// whatever one call happened to return and treated the untouched rest of the
					// buffer - zeroes - as payload; Stream.Read is explicitly allowed to return
					// fewer bytes than asked for, which is what CA2022 flags.
					gZipStream.CopyTo(decompressed);
					return Encoding.UTF8.GetString(decompressed.ToArray());
				}
			}
			catch (Exception ex)
			{
				// Empty is also what a caller gets for "this was never compressed"
				// (ModAssets.ImportFromClipboard falls through to a raw-JSON parse on either), so
				// without this line a genuinely corrupt payload is indistinguishable from plain text
				// and leaves no trace at all.
				SgtLogger.warning("DecompressString failed: " + ex.Message);
				return string.Empty;
			}
		}

	}
}
