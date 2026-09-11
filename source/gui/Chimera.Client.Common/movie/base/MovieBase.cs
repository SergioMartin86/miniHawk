using System.Collections.Generic;
using Chimera.Emulation.Common;

namespace Chimera.Client.Common
{
	public abstract partial class MovieBase : BasicMovieInfo, IMovie, IDisposable
	{
		private IController _defaultValueController;
		private ControllerDefinition _defaultValueControllerDefinition;
		private string _defaultValueControllerLogKey;

		/// <summary>
		/// A controller with nothing pressed on it, shaped like the entries of
		/// THIS movie's input log.
		/// </summary>
		/// <remarks>
		/// Neither half of that shape is available at construction time, and
		/// neither stays put: a project is read before its core boots, so the
		/// first caller here is handed the null emulator's controller, which
		/// knows none of the movie's axes. An entry generated from that one has
		/// no axis fields at all, and the next read of the frame it was written
		/// to cannot parse it - which is what made clearing frames take the
		/// piano roll down (issue #54). So what this was built from is
		/// remembered, and it is built again the moment either half changes.
		/// </remarks>
		protected IController DefaultValueController
		{
			get
			{
				var definition = Session.MovieController.Definition;
				if (_defaultValueController is null
					|| !ReferenceEquals(_defaultValueControllerDefinition, definition)
					|| _defaultValueControllerLogKey != LogKey)
				{
					_defaultValueController = new MovieController(definition, LogKey);
					_defaultValueControllerDefinition = definition;
					_defaultValueControllerLogKey = LogKey;
				}
				return _defaultValueController;
			}
		}

		public MovieBase(IMovieSession session, string filename) : base(filename)
		{
			Session = session;
			Header[HeaderKeys.MovieVersion] = "Chimera v1.0.0";
		}

		public virtual void Attach(IEmulator emulator)
		{
			Emulator = emulator;
		}

		private ControllerDefinition/*?*/ _activeControllerInputs = null;
		public virtual ControllerDefinition/*?*/ ActiveControllerInputs
		{
			get => _activeControllerInputs;
			set
			{
				value?.AssertImmutable();
				_activeControllerInputs = value;
			}
		}

		protected bool IsAttached() => Emulator != null;

		public IEmulator Emulator { get; private set; }
		public IMovieSession Session { get; }

		protected bool MakeBackup { get; set; } = true;

		public abstract string PreferredExtension { get; }

		public event EventHandler ChangesChanged;

		private bool _changes;
		public bool Changes
		{
			get => _changes;
			protected set
			{
				if (_changes != value)
				{
					_changes = value;
					ChangesChanged?.Invoke(this, EventArgs.Empty);
				}
			}
		}
		public bool IsCountingRerecords { get; set; } = true;

		public override int FrameCount => Log.Count;
		public int InputLogLength => Log.Count;

		public IStringLog GetLogEntries() => Log;

		public void CopyLog(IEnumerable<string> log)
		{
			Log.Clear();
			foreach (var entry in log)
			{
				Log.Add(entry);
			}
		}

		public void AppendFrame(IController source)
		{
			if (ActiveControllerInputs != null)
			{
				source = new MultitrackAdapter(source, new MovieController(Session.MovieController.Definition, LogKey), ActiveControllerInputs);
			}

			Log.Add(LogEntryGenerator.GenerateLogEntry(source));
			Changes = true;
		}

		public virtual void RecordFrame(int frame, IController source)
		{
			if (Session.Settings.VBAStyleMovieLoadState)
			{
				if (Emulator.Frame < Log.Count)
				{
					Truncate(Emulator.Frame);
				}
			}

			PokeFrame(frame, source);
		}

		public virtual void Truncate(int frame)
		{
			if (frame < Log.Count)
			{
				if (ActiveControllerInputs != null)
				{
					for (int i = frame; i < Log.Count; i++)
						PokeFrameCore(i, DefaultValueController);
					string defaultEntry = LogEntryGenerator.EmptyEntry(DefaultValueController);
					int firstDefault = Log.Count;
					while (firstDefault > frame && Log[firstDefault - 1] == defaultEntry)
						firstDefault--;
					frame = firstDefault;
				}

				Log.RemoveRange(frame, Log.Count - frame);
				Changes = true;
			}
		}

		public IMovieController GetInputState(int frame)
		{
			if (frame < FrameCount && frame >= -1)
			{
				MovieController controller = new(Session.MovieController.Definition, LogKey);
				controller.SetFromMnemonic(frame >= 0 ? Log[frame] : LogEntryGenerator.EmptyEntry(controller));
				return controller;
			}

			return null;
		}

		public virtual void PokeFrame(int frame, IController source) => PokeFrameCore(frame, source);

		/// <summary>
		/// The poke itself, bypassing subclass bookkeeping - Truncate's multitrack
		/// loop uses this, because a virtual PokeFrame would re-enter the TAS
		/// movie's change log per frame and corrupt the surrounding undo batch.
		/// </summary>
		protected void PokeFrameCore(int frame, IController source)
		{
			if (ActiveControllerInputs != null)
			{
				source = new MultitrackAdapter(source, GetInputState(frame) ?? DefaultValueController, ActiveControllerInputs);
			}

			SetFrameAt(frame, LogEntryGenerator.GenerateLogEntry(source));
			Changes = true;
		}

		/// <summary>
		/// Does not use <see cref="ActiveControllerInputs"/>.
		/// </summary>
		protected void SetFrameAt(int frameNum, string frame)
		{
			if (Log.Count > frameNum)
			{
				Log[frameNum] = frame;
			}
			else
			{
				Log.Add(frame);
			}
		}
	}
}
