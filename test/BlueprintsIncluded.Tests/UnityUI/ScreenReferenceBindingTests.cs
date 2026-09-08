using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Xunit;

namespace BlueprintsIncluded.Tests.UnityUI
{
	/// <summary>
	/// The FUI screens and list-entry components declare their child-widget references as
	/// non-nullable fields with a <c>= null!;</c> initializer and wire them up by hand in an
	/// <c>Init()</c> / <c>OnPrefabInit()</c> method. The <c>= null!</c> silences the nullable
	/// warning but gives no signal if a field is declared and never bound — that only shows
	/// up as a <see cref="NullReferenceException"/> the first time the widget is touched.
	///
	/// This test walks every <c>KMonoBehaviour</c> in the mod (screens derive from
	/// <c>KScreen : KMonoBehaviour</c>; the list entries derive from it directly). For each,
	/// it takes the <b>non-nullable</b> <see cref="Component"/> / <see cref="GameObject"/>
	/// fields <b>declared on that class</b> and checks each one is assigned somewhere in the
	/// class's own code — an <c>stfld</c>/<c>ldflda</c> scan over its methods, ignoring the
	/// constructor so the <c>= null!</c> initializer does not count. A field that fails this
	/// is "declared but never bound": wire it up, or make it nullable.
	///
	/// Two kinds of field are deliberately out of scope:
	/// <list type="bullet">
	///   <item><c>BindingFlags.DeclaredOnly</c> drops fields <i>inherited</i> from
	///     <c>FScreen</c> / <c>KScreen</c> / <c>KMonoBehaviour</c>.</item>
	///   <item>The <see cref="KleiInjected"/> attribute check drops fields declared on the
	///     class but populated by Klei's reflection rather than by our code
	///     (<c>[MyCmpGet]</c>, <c>[MyCmpReq]</c>, <c>[Serialize]</c>, …).</item>
	/// </list>
	///
	/// Needs the real Klei assemblies loaded (to see the <c>KMonoBehaviour</c> base type),
	/// so it is gated like the other game-coupled tests. It cannot catch a wrong
	/// <c>transform.Find</c> path (runtime only) — it catches "forgot to bind it at all".
	/// </summary>
	public class ScreenReferenceBindingTests
	{
		[RequiresGameInstallFact]
		public void EveryNonNullableWidgetFieldIsBound()
		{
			var offenders = new List<string>();

			foreach (var type in LoadTypes(typeof(BlueprintsV2.BlueprintData.Blueprint).Assembly))
			{
				if (type is null || type.IsAbstract || !typeof(KMonoBehaviour).IsAssignableFrom(type))
					continue;

				var mustBind = UiReferenceFields(type).ToList();
				if (mustBind.Count == 0)
					continue;

				var assigned = FieldsAssignedOutsideConstructor(type);

				offenders.AddRange(mustBind
					.Where(f => !assigned.Contains(f))
					.Select(f => $"{type.FullName}.{f.Name}"));
			}

			Assert.True(
				offenders.Count == 0,
				"non-nullable UI reference field(s) never assigned outside the constructor — wire them up " +
				"in Init()/OnPrefabInit() or make them nullable:\n  " + string.Join("\n  ", offenders.OrderBy(x => x)));
		}

		private static IEnumerable<Type?> LoadTypes(Assembly asm)
		{
			try { return asm.GetTypes(); }
			catch (ReflectionTypeLoadException e) { return e.Types; }
		}

		// ----- which fields must be bound -----

		private static readonly NullabilityInfoContext Nullability = new();

		// Fields carrying one of these are populated by Klei's KMonoBehaviour manager / the
		// save-load serializer, not by our own code, so the "must be assigned somewhere"
		// rule does not apply. Klei's attribute classes are named without the "Attribute"
		// suffix, so AttributeType.Name is e.g. "MyCmpGet", not "MyCmpGetAttribute".
		private static readonly HashSet<string> KleiInjected = new(StringComparer.Ordinal)
		{
			"MyCmpGet", "MyCmpReq", "MyCmpAdd", "MyCmpSet", "Serialize", "SerializeField",
		};

		private static IEnumerable<FieldInfo> UiReferenceFields(Type t)
		{
			foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				if (f.IsInitOnly || f.IsLiteral)
					continue;
				if (f.FieldType != typeof(GameObject) && !typeof(Component).IsAssignableFrom(f.FieldType))
					continue;
				if (f.GetCustomAttributesData().Any(a => KleiInjected.Contains(a.AttributeType.Name)))
					continue;
				if (Nullability.Create(f).WriteState == NullabilityState.Nullable)
					continue;
				yield return f;
			}
		}

		// ----- which fields the code assigns (stfld / ldflda), ignoring constructors -----

		private static HashSet<FieldInfo> FieldsAssignedOutsideConstructor(Type type)
		{
			var result = new HashSet<FieldInfo>();
			Scan(type, type, result);
			// lambdas / local functions get lowered into nested compiler-generated types;
			// `this.field = ...` inside one still emits a stfld against the outer field.
			foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
				Scan(type, nested, result);
			return result;
		}

		private static void Scan(Type owner, Type scan, HashSet<FieldInfo> result)
		{
			const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
			var genericArgs = owner.IsGenericType ? owner.GetGenericArguments() : null;

			foreach (var m in scan.GetMethods(all).Concat<MethodBase>(scan.GetConstructors(all)))
			{
				if (scan == owner && m is ConstructorInfo)
					continue; // the `= null!` initializer lives here — do not count it

				byte[]? il;
				try { il = m.GetMethodBody()?.GetILAsByteArray(); }
				catch { continue; }
				if (il == null)
					continue;

				foreach (int token in FieldStoreTokens(il))
				{
					try
					{
						var f = scan.Module.ResolveField(token, genericArgs, null);
						if (f?.DeclaringType == owner)
							result.Add(f);
					}
					catch { /* member ref we can't resolve — ignore */ }
				}
			}
		}

		private static readonly Dictionary<short, OpCode> OpCodesByValue =
			typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
				.Select(f => f.GetValue(null)).OfType<OpCode>().ToDictionary(op => op.Value);

		/// <summary>Metadata tokens of every field targeted by <c>stfld</c> or <c>ldflda</c> (address-of covers <c>out</c> params).</summary>
		private static IEnumerable<int> FieldStoreTokens(byte[] il)
		{
			int pos = 0;
			while (pos < il.Length)
			{
				short code = il[pos++];
				if (code == 0xFE)
					code = (short)(0xFE00 | il[pos++]);

				if (!OpCodesByValue.TryGetValue(code, out var op))
					yield break; // unknown opcode — stop rather than misread operands

				if (op == OpCodes.Stfld || op == OpCodes.Ldflda)
					yield return BitConverter.ToInt32(il, pos);

				pos += OperandSize(op.OperandType, il, pos);
			}
		}

		private static int OperandSize(OperandType t, byte[] il, int pos) => t switch
		{
			OperandType.InlineNone => 0,
			OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
			OperandType.InlineVar => 2,
			OperandType.InlineI8 or OperandType.InlineR => 8,
			OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
			_ => 4,
		};
	}
}
