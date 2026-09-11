using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Tests.Emulation.Common
{
	/// <summary>
	/// The video declaration's two GPU answers. Both are read once, deep in a
	/// path nobody looks at, and both are silently harmless when wrong - a core
	/// that should warm up and reports zero simply draws a slightly wrong frame
	/// at the end of a seek, which is exactly the bug the warm-up exists for.
	/// So the plumbing is pinned rather than trusted.
	/// </summary>
	[TestClass]
	public class WaterboxVideoTests
	{
		[TestMethod]
		public void ADeclaredWarmupReachesTheCore()
		{
			WaterboxConfig declared = new() { Video = new() { RenderWarmupFrames = 10 } };
			Assert.AreEqual(10, declared.Video.RenderWarmupFrames);

			// what a package that says nothing means, which is almost all of them
			WaterboxConfig silent = new() { Video = new() };
			Assert.AreEqual(0, silent.Video.RenderWarmupFrames,
				"a core that does not ask for a warm-up must not get one");
		}

		[TestMethod]
		public void AMissingVideoBlockIsNotAWarmup()
		{
			WaterboxConfig none = new();
			Assert.IsNull(none.Video);
		}

		/// <summary>
		/// The package is JSON and the property names are the contract; a rename
		/// on either side is a warm-up that quietly becomes zero.
		/// </summary>
		[TestMethod]
		public void TheWarmupIsReadFromTheNameThePackageUses()
		{
			var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<WaterboxConfig>(
				"{\"video\":{\"renderWarmupFrames\":7,\"gpuStatesSurviveTheContext\":true}}");
			Assert.AreEqual(7, cfg.Video.RenderWarmupFrames);
			Assert.IsTrue(cfg.Video.GpuStatesSurviveTheContext);
		}
	}
}
