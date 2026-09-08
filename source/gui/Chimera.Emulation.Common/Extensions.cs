#nullable disable

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Chimera.Common;
using Chimera.Common.PathExtensions;
using Chimera.Common.StringExtensions;

namespace Chimera.Emulation.Common
{
	public static class EmulatorExtensions
	{
		/// <summary>Chimera has no display-name table for systems; the ID is the name. Cores/packages may prettify in their own UI.</summary>
		public static string SystemIDToDisplayName(string sysID)
			=> sysID;

		/// <summary>
		/// How a core introduces itself: name, author, version. A core that knows its
		/// own identity at runtime (<see cref="ICoreIdentity"/>) is asked, because one
		/// adapter type can serve many packages and the class attribute could then only
		/// name the adapter. Everything else falls back to the attribute on its class.
		/// </summary>
		public static CoreAttribute Attributes(this IEmulator core)
		{
			return (core as ICoreIdentity)?.CoreIdentity
				?? (CoreAttribute)Attribute.GetCustomAttribute(core.GetType(), typeof(CoreAttribute));
		}

		// todo: most of the special cases involving the NullEmulator should probably go away
		public static bool IsNull(this IEmulator core)
		{
			return core == null || core is NullEmulator;
		}

		public static bool HasVideoProvider(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<IVideoProvider>();
		}

		public static IVideoProvider AsVideoProvider(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IVideoProvider>();
		}

		/// <summary>
		/// Returns the core's VideoProvider, or a suitable dummy provider
		/// </summary>
		public static IVideoProvider AsVideoProviderOrDefault(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IVideoProvider>()
				?? NullVideo.Instance;
		}

		public static bool HasSoundProvider(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<ISoundProvider>();
		}

		public static ISoundProvider AsSoundProvider(this IEmulator core)
		{
			return core.ServiceProvider.GetService<ISoundProvider>();
		}

		private static readonly ConditionalWeakTable<IEmulator, ISoundProvider> CachedNullSoundProviders = new ConditionalWeakTable<IEmulator, ISoundProvider>();

		/// <summary>
		/// returns the core's SoundProvider, or a suitable dummy provider
		/// </summary>
		public static ISoundProvider AsSoundProviderOrDefault(this IEmulator core)
		{
			return core.ServiceProvider.GetService<ISoundProvider>()
				?? CachedNullSoundProviders.GetValue(core, e => new NullSound(core.VsyncNumerator(), core.VsyncDenominator()));
		}

		public static bool HasMemoryDomains(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<IMemoryDomains>();
		}

		public static IMemoryDomains AsMemoryDomains(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IMemoryDomains>();
		}

		/// <summary>
		/// The core's own version - for a package, the commit its published build was made
		/// from. Empty when the core does not say, which a movie then does not claim.
		/// </summary>
		public static string CoreVersion(this IEmulator core)
			=> core.Attributes() is PortedCoreAttribute ported ? ported.PortedVersion : "";

		public static bool HasSavestates(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<IStatable>();
		}

		public static IStatable AsStatable(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IStatable>();
		}

		/// <summary>Where the machine has been, kept by the engine (docs/state-manager.md).</summary>
		public static IStateHistory AsStateHistory(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IStateHistory>();
		}

		public static bool CanPollInput(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<IInputPollable>();
		}

		public static ICorePrecompile AsPrecompile(this IEmulator core)
			=> core.ServiceProvider.GetService<ICorePrecompile>();

		public static IInputPollable AsInputPollable(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IInputPollable>();
		}

		public static bool InputCallbacksAvailable(this IEmulator core)
		{
			// TODO: this is a pretty ugly way to handle this
			var pollable = core?.ServiceProvider.GetService<IInputPollable>();
			if (pollable != null)
			{
				try
				{
					var callbacks = pollable.InputCallbacks;
					return true;
				}
				catch (NotImplementedException)
				{
					return false;
				}
			}

			return false;
		}

		public static bool HasDriveLight(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<IDriveLight>();
		}

		public static IDriveLight AsDriveLight(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IDriveLight>();
		}

		public static bool CanDebug(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<IDebuggable>();
		}

		public static IDebuggable AsDebuggable(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IDebuggable>();
		}

		public static bool CpuTraceAvailable(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<ITraceable>();
		}

		public static ITraceable AsTracer(this IEmulator core)
		{
			return core.ServiceProvider.GetService<ITraceable>();
		}

		public static bool MemoryCallbacksAvailable(this IEmulator core)
		{
			// TODO: this is a pretty ugly way to handle this
			var debuggable = core?.ServiceProvider.GetService<IDebuggable>();
			if (debuggable != null)
			{
				try
				{
					var callbacks = debuggable.MemoryCallbacks;
					return true;
				}
				catch (NotImplementedException)
				{
					return false;
				}
			}

			return false;
		}

		public static bool MemoryCallbacksAvailable(this IDebuggable core)
		{
			if (core == null)
			{
				return false;
			}

			try
			{
				var callbacks = core.MemoryCallbacks;
				return true;
			}
			catch (NotImplementedException)
			{
				return false;
			}
		}

		public static bool CanDisassemble(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<IDisassemblable>();
		}

		public static IDisassemblable AsDisassembler(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IDisassemblable>();
		}

		public static bool HasRegions(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<IRegionable>();
		}

		public static IRegionable AsRegionable(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IRegionable>();
		}

		public static bool CanCDLog(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<ICodeDataLogger>();
		}

		public static ICodeDataLogger AsCodeDataLogger(this IEmulator core)
		{
			return core.ServiceProvider.GetService<ICodeDataLogger>();
		}

		public static ILinkable AsLinkable(this IEmulator core)
		{
			return core.ServiceProvider.GetService<ILinkable>();
		}

		public static bool UsesLinkCable(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<ILinkable>();
		}

		public static bool HasBoardInfo(this IEmulator core)
		{
			return core != null && core.ServiceProvider.HasService<IBoardInfo>();
		}

		public static IBoardInfo AsBoardInfo(this IEmulator core)
		{
			return core.ServiceProvider.GetService<IBoardInfo>();
		}

		public static (int X, int Y) ScreenLogicalOffsets(this IEmulator core)
		{
			if (core != null && core.ServiceProvider.HasService<IVideoLogicalOffsets>())
			{
				var offsets = core.ServiceProvider.GetService<IVideoLogicalOffsets>();
				return (offsets.ScreenX, offsets.ScreenY);
			}

			return (0, 0);
		}

		public static string RomDetails(this IEmulator core)
		{
			if (core != null && core.ServiceProvider.HasService<IRomInfo>())
			{
				return core.ServiceProvider.GetService<IRomInfo>().RomDetails;
			}

			return "";
		}

		public static int VsyncNumerator(this IEmulator core)
		{
			if (core?.AsVideoProvider() is { } videoCore)
			{
				return videoCore.VsyncNumerator;
			}

			return 60;
		}

		public static int VsyncDenominator(this IEmulator core)
		{
			if (core?.AsVideoProvider() is { } videoCore)
			{
				return videoCore.VsyncDenominator;
			}

			return 1;
		}

		public static double VsyncRate(this IEmulator core)
		{
			return core.VsyncNumerator() / (double)core.VsyncDenominator();
		}

		public static bool HasCycleTiming(this IEmulator core)
			=> core != null && core.ServiceProvider.HasService<ICycleTiming>();

		public static ICycleTiming AsCycleTiming(this IEmulator core)
			=> core.ServiceProvider.GetService<ICycleTiming>();

		public static bool IsImplemented(this MethodInfo info)
		{
			return !info.GetCustomAttributes(false).Any(a => a is FeatureNotImplementedAttribute);
		}

		/// <summary>
		/// Gets a list of boolean button names. If a controller number is specified, only returns button names
		/// (without the "P" prefix) that match that controller number. If a controller number is NOT specified,
		/// then all button names are returned.
		/// <br/>For example, consider <c>controller.Definition is { BoolButtons = [ "P1 A", "P1 B", "P2 B", "P2 C" ] }</c>.
		/// Then:<list type="bullet">
		/// <item><description><c>controller.ToBoolButtonNameList(1)</c> --> <c>[ "A", "B" ]</c></description></item>
		/// <item><description><c>controller.ToBoolButtonNameList(2)</c> --> <c>[ "B", "C" ]</c></description></item>
		/// <item><description><c>controller.ToBoolButtonNameList(null)</c> --> <c>[ "P1 A", "P1 B", "P2 B", "P2 C" ]</c></description></item>
		/// </list>
		/// </summary>
		public static List<string> ToBoolButtonNameList(this IController controller, int? controllerNum = null)
		{
			return ToControlNameList(controller.Definition.BoolButtons, controllerNum);
		}

		/// <summary>as <see cref="ToBoolButtonNameList"/>, but with axis names</summary>
		public static List<string> ToAxisControlNameList(this IController controller, int? controllerNum = null)
		{
			return ToControlNameList(controller.Definition.Axes.Keys, controllerNum);
		}

		private static List<string> ToControlNameList(IEnumerable<string> buttonList, int? controllerNum = null)
		{
			var buttons = buttonList;
			if (controllerNum is int n)
			{
				var pfx = $"P{n} ";
				buttons = buttons.Select(buttonName => (ButtonName: buttonName, Tail: buttonName.RemovePrefix(pfx)))
					.Where(static tuple => !object.ReferenceEquals(tuple.Tail, tuple.ButtonName))
					.Select(static tuple => tuple.Tail);
			}
			return buttons.ToList();
		}

		public static IReadOnlyDictionary<string, object> ToDictionary(this IController controller, int? controllerNum = null)
		{
			var dict = new Dictionary<string, object>();
			if (controllerNum == null)
			{
				foreach (var buttonName in controller.Definition.BoolButtons) dict[buttonName] = controller.IsPressed(buttonName);
				foreach (var axisName in controller.Definition.Axes.Keys) dict[axisName] = controller.AxisValue(axisName);
				return dict;
			}
			var prefix = $"P{controllerNum} ";
			foreach (var buttonName in controller.Definition.BoolButtons)
			{
				var s = buttonName.RemovePrefix(prefix);
				if (ReferenceEquals(s, buttonName)) continue; // did not start with prefix
				dict[s] = controller.IsPressed(buttonName);
			}
			foreach (var axisName in controller.Definition.Axes.Keys)
			{
				var s = axisName.RemovePrefix(prefix);
				if (ReferenceEquals(s, axisName)) continue; // did not start with prefix
				dict[s] = controller.AxisValue(axisName);
			}
			return dict;
		}

		public static string FilesystemSafeName(this IGameInfo game)
			=> game.Name.Replace('/', '+') // '/' is the path dir separator, obviously (methods in Path will treat it as such, even on Windows)
				.Replace('|', '+') // '|' is the filename-member separator for archives in ChimeraFile
				.Replace(":", " -") // ':' is the path separator in lists (Path.GetFileName will drop all but the last entry in such a list)
				.Replace("\"", "") // '"' is just annoying as it needs escaping on the command-line
				.RemoveInvalidFileSystemChars()
				.RemoveSuffix('.'); // trailing '.' would be duplicated when file extension is added

		/// <summary>
		/// Adds an axis to the receiver <see cref="ControllerDefinition"/>, and returns it.
		/// The new axis will appear after any that were previously defined.
		/// </summary>
		/// <param name="constraint">pass only for one axis in a pair, by convention the X axis</param>
		/// <returns>identical reference to <paramref name="def"/>; the object is mutated</returns>
		public static ControllerDefinition AddAxis(this ControllerDefinition def, string name, Range<int> range, int neutral, bool isReversed = false, AxisConstraint constraint = null)
		{
			def.Axes.Add(name, new AxisSpec(range, neutral, isReversed, constraint));
			return def;
		}

		/// <summary>
		/// Adds an X/Y pair of axes to the receiver <see cref="ControllerDefinition"/>, and returns it.
		/// The new axes will appear after any that were previously defined.
		/// </summary>
		/// <param name="nameFormat">format string e.g. <c>"P1 Left {0}"</c> (will be used to interpolate <c>"X"</c> and <c>"Y"</c>)</param>
		/// <returns>identical reference to <paramref name="def"/>; the object is mutated</returns>
		public static ControllerDefinition AddXYPair(this ControllerDefinition def, string nameFormat, AxisPairOrientation pDir, Range<int> rangeX, int neutralX, Range<int> rangeY, int neutralY, AxisConstraint constraint = null)
		{
			var yAxisName = string.Format(nameFormat, "Y");
			var finalConstraint = constraint ?? new NoOpAxisConstraint(yAxisName);
			return def.AddAxis(string.Format(nameFormat, "X"), rangeX, neutralX, ((byte) pDir & 2) != 0, finalConstraint)
				.AddAxis(yAxisName, rangeY, neutralY, ((byte) pDir & 1) != 0);
		}

		/// <summary>
		/// Adds an X/Y pair of axes to the receiver <see cref="ControllerDefinition"/>, and returns it.
		/// The new axes will appear after any that were previously defined.
		/// </summary>
		/// <param name="nameFormat">format string e.g. <c>"P1 Left {0}"</c> (will be used to interpolate <c>"X"</c> and <c>"Y"</c>)</param>
		/// <returns>identical reference to <paramref name="def"/>; the object is mutated</returns>
		public static ControllerDefinition AddXYPair(this ControllerDefinition def, string nameFormat, AxisPairOrientation pDir, Range<int> rangeBoth, int neutralBoth, AxisConstraint constraint = null)
			=> def.AddXYPair(nameFormat, pDir, rangeBoth, neutralBoth, rangeBoth, neutralBoth, constraint);

		/// <summary>
		/// Adds an X/Y/Z triple of axes to the receiver <see cref="ControllerDefinition"/>, and returns it.
		/// The new axes will appear after any that were previously defined.
		/// </summary>
		/// <param name="nameFormat">format string e.g. <c>"P1 Tilt {0}"</c> (will be used to interpolate <c>"X"</c>, <c>"Y"</c>, and <c>"Z"</c>)</param>
		/// <returns>identical reference to <paramref name="def"/>; the object is mutated</returns>
		public static ControllerDefinition AddXYZTriple(this ControllerDefinition def, string nameFormat, Range<int> rangeAll, int neutralAll)
			=> def.AddAxis(string.Format(nameFormat, "X"), rangeAll, neutralAll)
				.AddAxis(string.Format(nameFormat, "Y"), rangeAll, neutralAll)
				.AddAxis(string.Format(nameFormat, "Z"), rangeAll, neutralAll);

		public static AxisSpec With(this in AxisSpec spec, Range<int> range, int neutral) => new AxisSpec(range, neutral, spec.IsReversed, spec.Constraint);

		public static bool IsEnabled(this ITraceable core) => core.Sink is not null;

		/// <remarks>TODO no-op instead of NRE when not "enabled"?</remarks>
		public static void Put(this ITraceable core, TraceInfo info) => core.Sink.Put(info);
	}
}
