using Chimera.Client.GUI;
using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Tests.Client.GUI
{
	/// <summary>
	/// The renderer, chosen beside the core on page one.
	///
	/// It is not a property of the machine - it is the same machine, drawn
	/// twice - so it is asked once, in the same place, for every core: a core
	/// that offers a choice offers it here, and a core that draws one way says
	/// so rather than staying silent.
	/// </summary>
	[TestClass]
	public class WizardRendererTests
	{
		private static WaterboxConfig CfgWithRenderers()
			=> WaterboxConfig.FromJson("""
				{
				  "coreName": "two-renderers",
				  "systemId": "DC",
				  "video": { "width": 640, "height": 480 },
				  "audio": { "samplesPerFrame": 1024 },
				  "input": { "buttons": [] },
				  "settings": [
				    { "name": "region", "type": "enum", "options": ["usa", "japan"], "default": "usa" },
				    { "name": "renderer", "type": "enum", "options": ["software", "opengl", "opengl-hw"], "default": "software" }
				  ]
				}
				""");

		private static WaterboxConfig CfgWithoutRenderers()
			=> WaterboxConfig.FromJson("""
				{
				  "coreName": "one-renderer",
				  "systemId": "NES",
				  "video": { "width": 256, "height": 240 },
				  "audio": { "samplesPerFrame": 1024 },
				  "input": { "buttons": [] },
				  "settings": [
				    { "name": "region", "type": "enum", "options": ["ntsc", "pal"], "default": "ntsc" }
				  ]
				}
				""");

		private static NewProjectWizard MakeForm(WaterboxConfig cfg)
		{
			NewProjectWizard form = new([ ], static _ => [ ]);
			form.Show();
			form.UseSettingsFrom(cfg);
			return form;
		}

		[TestMethod]
		public void ACoreThatOffersRenderersOffersThemInDeclarationOrder()
		{
			using var form = MakeForm(CfgWithRenderers());
			CollectionAssert.AreEqual(new[] { "software", "opengl", "opengl-hw" }, form.RendererOptions);
			Assert.IsTrue(form.RendererIsAChoice, "more than one renderer is a choice");
		}

		[TestMethod]
		public void ItSpellsOutWhatEachRendererActuallyIs()
		{
			using var form = MakeForm(CfgWithRenderers());
			CollectionAssert.AreEqual(new[] { "Software (Native)", "Software (OpenGL)", "Hardware (OpenGL)" }, form.RendererLabels,
				"the first two draw on the CPU; the third leaves the sandbox for a GPU");

			using var one = MakeForm(CfgWithoutRenderers());
			CollectionAssert.AreEqual(new[] { "Software (Native)" }, one.RendererLabels,
				"a core that names no renderer still says what it does");
		}

		[TestMethod]
		public void ChoosingHardwareSaysWhatItCosts()
		{
			using var form = MakeForm(CfgWithRenderers());
			Assert.AreEqual("", form.RendererCaveatText,
				"a software renderer has nothing to be careful about");

			form.SetRenderer("opengl-hw");
			StringAssert.Contains(form.RendererCaveatText, "video output",
				"the cost - a picture that can differ elsewhere - is said where the choice is made");
			StringAssert.Contains(form.RendererCaveatText, "greenzone",
				"and so is what to do about it");

			form.SetRenderer("software");
			Assert.AreEqual("", form.RendererCaveatText, "and it goes away again");
		}

		[TestMethod]
		public void OnlyTheHardwareOnesCarryTheWarning()
		{
			Assert.IsTrue(NewProjectWizard.IsHardware("opengl-hw"));
			Assert.IsFalse(NewProjectWizard.IsHardware("opengl"));
			Assert.IsFalse(NewProjectWizard.IsHardware("software"));
			Assert.IsFalse(NewProjectWizard.IsHardware(null));
		}

		[TestMethod]
		public void ItStartsOnTheRendererTheCoreDeclaredAsItsDefault()
		{
			using var form = MakeForm(CfgWithRenderers());
			Assert.AreEqual("software", form.ChosenRenderer);
		}

		[TestMethod]
		public void ACoreThatDrawsOneWaySaysSoAndCannotBeChanged()
		{
			using var form = MakeForm(CfgWithoutRenderers());
			CollectionAssert.AreEqual(new[] { "default" }, form.RendererOptions);
			Assert.IsFalse(form.RendererIsAChoice, "one renderer is not a choice");
		}

		[TestMethod]
		public void TheChoiceIsRecordedAsASettingLikeAnyOther()
		{
			using var form = MakeForm(CfgWithRenderers());
			form.SetRenderer("opengl");
			Assert.AreEqual("opengl", form.ChosenRenderer);
			Assert.AreEqual("opengl", form.SettingValue("renderer"),
				"the project records what the dropdown chose");
		}

		[TestMethod]
		public void ItIsNotAskedAgainAmongTheSettings()
		{
			using var form = MakeForm(CfgWithRenderers());
			CollectionAssert.Contains(form.ExposedSettingNames, "region");
			CollectionAssert.DoesNotContain(form.ExposedSettingNames, "renderer",
				"it is chosen beside the core, so the settings page does not ask again");
		}

		[TestMethod]
		public void ACoreWithNoRendererSettingRecordsNothingForIt()
		{
			using var form = MakeForm(CfgWithoutRenderers());
			Assert.IsNull(form.SettingValue("renderer"),
				"a core that never declared one is not given a value it cannot read");
		}
	}
}
