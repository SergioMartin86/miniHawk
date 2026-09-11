using System.ComponentModel;
using System.Linq;

using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Tests.Emulation.Common
{
	/// <summary>
	/// A waterbox core's settings dialog is generated: the package declares each
	/// setting and the frontend synthesizes a property grid row from it. So the
	/// declaration IS the UI, and these check what a given declaration turns into.
	/// </summary>
	[TestClass]
	public class WaterboxSettingsTests
	{
		private static WaterboxConfig.SettingDecl Enum(string name, string dflt, params string[] options)
			=> new() { Name = name, Type = "enum", Default = dflt, Options = options.ToList() };

		private static WaterboxConfig.SettingDecl Int(string name, int dflt, int? min = null, int? max = null)
			=> new() { Name = name, Type = "int", Default = dflt, Min = min, Max = max };

		/// <summary>
		/// A multi-machine core's settings mostly belong to one machine each. A Game
		/// Boy's DMG revision means nothing on a Mega Drive, and a Neo Geo's coin
		/// slots mean nothing anywhere else - so a declaration says which machine it
		/// is for, and the machine only ever sees its own.
		/// </summary>
		[TestMethod]
		public void AMachineOnlySeesTheSettingsThatBelongToIt()
		{
			WaterboxConfig cfg = new()
			{
				MachineSetting = "machine",
				Machines =
				[
					new() { Id = "GB", Label = "Game Boy", When = [ "gb" ] },
					new() { Id = "NG", Label = "Neo Geo", When = [ "ng" ] },
				],
				Settings =
				[
					new() { Name = "machine", Type = "enum", Default = "gb", Options = [ "gb", "ng" ] },
					new() { Name = "gb.fastBoot", Default = false, When = [ "gb" ] },
					new() { Name = "ng.settingsMode", Default = false, When = [ "ng" ] },
					new() { Name = "region", Type = "enum", Default = "ntsc", Options = [ "ntsc", "pal" ] },
				],
			};

			var gb = cfg.SettingsFor(cfg.Machines[0]).Select(static d => d.Name).ToList();
			CollectionAssert.Contains(gb, "gb.fastBoot");
			CollectionAssert.DoesNotContain(gb, "ng.settingsMode", "a Game Boy has no coin slot");
			CollectionAssert.Contains(gb, "region", "a setting that names no machine belongs to all of them");
			CollectionAssert.Contains(gb, "machine");

			var ng = cfg.SettingsFor(cfg.Machines[1]).Select(static d => d.Name).ToList();
			CollectionAssert.Contains(ng, "ng.settingsMode");
			CollectionAssert.DoesNotContain(ng, "gb.fastBoot");
		}

		/// <summary>
		/// The engine says which settings are exposed by INDEX into the list it
		/// was handed, which is the package's own. A machine's narrowed list is
		/// shorter and renumbered, so the two must not be confused: they were,
		/// and the effect was that every setting belonging to a machine
		/// disappeared from the wizard - a Neo Geo showed none of its DIP
		/// switches, a Game Boy no Fast Boot.
		/// </summary>
		[TestMethod]
		public void AMachineSettingIsFoundAtItsPackageIndex()
		{
			WaterboxConfig cfg = new()
			{
				MachineSetting = "machine",
				Machines =
				[
					new() { Id = "GB", Label = "Game Boy", When = [ "gb" ] },
					new() { Id = "NG", Label = "Neo Geo", When = [ "ng" ] },
				],
				Settings =
				[
					new() { Name = "machine", Type = "enum", Default = "gb", Options = [ "gb", "ng" ] },
					new() { Name = "gb.fastBoot", Default = false, When = [ "gb" ] },
					new() { Name = "ng.settingsMode", Default = false, When = [ "ng" ] },
					new() { Name = "ng.freePlay", Default = true, When = [ "ng" ] },
					new() { Name = "region", Type = "enum", Default = "ntsc", Options = [ "ntsc", "pal" ] },
				],
			};

			var ng = cfg.SettingsByDeclarationIndexFor(cfg.Machines[1])
				.Select(static d => d?.Name).ToList();
			Assert.AreEqual(cfg.Settings.Count, ng.Count,
				"one slot per package setting, whether or not this machine has it");
			CollectionAssert.AreEqual(
				new[] { "machine", null, "ng.settingsMode", "ng.freePlay", "region" },
				ng, "a Neo Geo keeps its own settings at the package's own indices");

			var gb = cfg.SettingsByDeclarationIndexFor(cfg.Machines[0])
				.Select(static d => d?.Name).ToList();
			CollectionAssert.AreEqual(
				new[] { "machine", "gb.fastBoot", null, null, "region" }, gb);
		}

		[TestMethod]
		public void ASettingThatNamesNoMachineAppliesToAnyOfThem()
		{
			WaterboxConfig.SettingDecl shared = new() { Name = "region" };
			Assert.IsTrue(shared.AppliesTo("gb"));
			Assert.IsTrue(shared.AppliesTo(null));

			WaterboxConfig.SettingDecl scoped = new() { Name = "gb.fastBoot", When = [ "gb", "gbc" ] };
			Assert.IsTrue(scoped.AppliesTo("gb"));
			Assert.IsTrue(scoped.AppliesTo("GBC"), "the machine setting's value is not case sensitive");
			Assert.IsFalse(scoped.AppliesTo("ng"));
			Assert.IsFalse(scoped.AppliesTo(null));
		}

		[TestMethod]
		public void TypeIsInferredWhenNotDeclared()
		{
			Assert.AreEqual("bool", new WaterboxConfig.SettingDecl { Default = true }.EffectiveType);
			Assert.AreEqual("int", new WaterboxConfig.SettingDecl { Default = 8 }.EffectiveType);
			Assert.AreEqual("enum", new WaterboxConfig.SettingDecl { Options = [ "a", "b" ] }.EffectiveType);
		}

		[TestMethod]
		public void JsonNumbersArriveAsLongsAndMustStillBeInts()
		{
			// Newtonsoft boxes every JSON integer as long; the grid edits an int
			var decl = Int("spriteLimit", 8);
			Assert.AreEqual(64, decl.Coerce(64L));
			Assert.AreEqual(64, decl.Coerce("64"), "and config files can hand back the string form");
		}

		[TestMethod]
		public void IntsAreClampedToTheDeclaredRange()
		{
			var decl = Int("spriteLimit", 8, min: 0, max: 64);
			Assert.AreEqual(64, decl.Coerce(9999), "a core that says 64 max must never be handed 9999");
			Assert.AreEqual(0, decl.Coerce(-1));
			Assert.AreEqual(8, decl.Coerce(8));
		}

		[TestMethod]
		public void AnUnknownEnumValueFallsBackToSomethingLegal()
		{
			// a stale config, or a package that dropped an option in a new version
			var decl = Enum("port1", "gamepad", "none", "gamepad", "fourScore");
			Assert.AreEqual("gamepad", decl.Coerce("zapper"), "the declared default is the first choice");
			Assert.AreEqual("gamepad", decl.Coerce(null));
			Assert.AreEqual("fourScore", decl.Coerce("fourScore"));
		}

		[TestMethod]
		public void AnUnknownValueFallsBackToTheFirstOptionWhenTheDefaultIsAlsoIllegal()
		{
			var decl = Enum("port1", "nonsense", "none", "gamepad");
			Assert.AreEqual("none", decl.Coerce("zapper"));
		}

		[TestMethod]
		public void GarbageDoesNotThrow()
		{
			// values come from a JSON file a user can edit
			Assert.AreEqual(0, Int("n", 0).Coerce("not a number"));
			Assert.AreEqual(false, new WaterboxConfig.SettingDecl { Type = "bool", Default = false }.Coerce("banana"));
		}

		[TestMethod]
		public void EachDeclarationBecomesOneGridRow()
		{
			WaterboxCoreSettings settings = new()
			{
				Declarations = [ Enum("port1", "gamepad", "none", "gamepad"), Int("spriteLimit", 8) ],
			};
			var props = TypeDescriptor.GetProperties(settings);
			Assert.AreEqual(2, props.Count);
			Assert.AreEqual(typeof(string), props["port1"].PropertyType);
			Assert.AreEqual(typeof(int), props["spriteLimit"].PropertyType);
		}

		[TestMethod]
		public void TheRowShowsTheCoresOwnWords()
		{
			var decl = Enum("port1", "gamepad", "none", "gamepad");
			decl.Display = "Left Port Peripheral";
			decl.Description = "What is plugged into controller port 1.";
			var prop = TypeDescriptor.GetProperties(new WaterboxCoreSettings { Declarations = [ decl ] })[0];
			Assert.AreEqual("Left Port Peripheral", prop.DisplayName);
			Assert.AreEqual("What is plugged into controller port 1.", prop.Description);
		}

		[TestMethod]
		public void TheDisplayNameFallsBackToTheKey()
			=> Assert.AreEqual("port1", TypeDescriptor.GetProperties(new WaterboxCoreSettings { Declarations = [ Enum("port1", "none", "none") ] })[0].DisplayName);

		[TestMethod]
		public void AnEnumRowOffersExactlyTheDeclaredOptions()
		{
			var prop = TypeDescriptor.GetProperties(new WaterboxCoreSettings
			{
				Declarations = [ Enum("port1", "gamepad", "none", "gamepad", "fourScore") ],
			})[0];
			Assert.IsTrue(prop.Converter.GetStandardValuesSupported(null));
			Assert.IsTrue(prop.Converter.GetStandardValuesExclusive(null), "free text would let the user name a peripheral the core has never heard of");
			CollectionAssert.AreEqual(
				new[] { "none", "gamepad", "fourScore" },
				prop.Converter.GetStandardValues(null).Cast<string>().ToList());
		}

		[TestMethod]
		public void AnUnsetRowReadsAsTheCoresDefault()
		{
			WaterboxCoreSettings settings = new() { Declarations = [ Int("spriteLimit", 8) ] };
			var prop = TypeDescriptor.GetProperties(settings)[0];
			Assert.AreEqual(8, prop.GetValue(settings));
			Assert.IsFalse(prop.ShouldSerializeValue(settings), "unchanged from the package default, so the grid must not bold it");
		}

		[TestMethod]
		public void EditingARowWritesThroughToTheValuesBag()
		{
			// the bag is what gets serialized into the config file and movie headers
			WaterboxCoreSettings settings = new() { Declarations = [ Int("spriteLimit", 8, min: 0, max: 64) ] };
			var prop = TypeDescriptor.GetProperties(settings)[0];
			prop.SetValue(settings, 64);
			Assert.AreEqual(64, settings.Values["spriteLimit"]);
			Assert.IsTrue(prop.ShouldSerializeValue(settings));

			prop.ResetValue(settings);
			Assert.AreEqual(8, settings.Values["spriteLimit"]);
		}

		[TestMethod]
		public void ASettingsObjectWithNoDeclarationsHasNoRows()
		{
			// how it arrives from the config file at startup, with no core loaded
			Assert.AreEqual(0, TypeDescriptor.GetProperties(new WaterboxCoreSettings()).Count);
		}

		[TestMethod]
		public void ValuesEqualIgnoresHowANumberWasSpelled()
		{
			// one side has been through a JSON round-trip, the other has not; a
			// spurious difference here would reboot the core for no reason
			WaterboxCoreSettings a = new() { Values = { ["spriteLimit"] = 8 } };
			WaterboxCoreSettings b = new() { Values = { ["spriteLimit"] = 8L } };
			Assert.IsTrue(a.ValuesEqual(b));

			b.Values["spriteLimit"] = 64;
			Assert.IsFalse(a.ValuesEqual(b));
		}

		[TestMethod]
		public void CloneDoesNotShareTheValuesBag()
		{
			WaterboxCoreSettings original = new() { Values = { ["port1"] = "gamepad" } };
			var clone = original.Clone();
			clone.Values["port1"] = "fourScore";
			Assert.AreEqual("gamepad", original.Values["port1"], "the dialog edits a clone; the core must not see it until OK");
		}
	}
}
