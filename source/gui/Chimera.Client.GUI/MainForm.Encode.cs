#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Linq;

using Chimera.Client.Common;
using Chimera.Common;
using Chimera.Emulation.Common;

namespace Chimera.Client.GUI
{
	/// <summary>
	/// Encoding a video, start to finish, as one act.
	///
	/// The old way was three unrelated commands a person had to assemble in the
	/// right order - start the writer, play the movie, stop the writer - and
	/// getting the order wrong produced a file that was empty, short or never
	/// finished. Here the whole thing is one job: it winds the run to the first
	/// frame asked for, reproduces it as fast as the machine will go, writes each
	/// frame out, closes the file, and puts the emulator back exactly where the
	/// person left it.
	///
	/// The run loop drives it. <see cref="StepRunLoop_Core"/> already knows how to
	/// emulate a frame, dump it and keep the sound fed; a job only makes that
	/// happen every iteration, unthrottled, and decides when to stop. Nothing here
	/// emulates anything itself, which is what keeps the encoded frames identical
	/// to the played ones.
	/// </summary>
	public partial class MainForm
	{
		private VideoEncodeJob? _encode;

		/// <summary>True while a video is being written, from Start Encode to the closed file.</summary>
		public bool IsEncodingVideo => _encode?.IsRunning is true;

		/// <summary>
		/// The run loop asks this whether it should keep emulating regardless of
		/// pause, input and every other ordinary reason to sit still.
		/// </summary>
		private bool EncodeWantsFrame => _encode?.IsRunning is true;

		private EncodeVideoForm? _encodeDialog;

		/// <summary>
		/// Opens Encode Video.
		///
		/// Modeless on purpose. A modal window runs its own message loop, and the
		/// loop that would be running is the one that emulates frames - a modal
		/// Encode Video would sit there unable to encode anything.
		/// </summary>
		public void EncodeVideoDialog()
		{
			if (_encodeDialog is { IsDisposed: false })
			{
				_encodeDialog.Focus();
				return;
			}

			if (MovieSession.Movie is not ITasMovie movie || movie.NotActive())
			{
				ShowMessageBox(owner: null, "Open a project first: a video is an encode of a run.");
				return;
			}

			var markers = movie.Markers.ToList();
			var extension = EncodeVideoForm.ExtensionOf(Config.FFmpegCustomCommand) ?? "mp4";
			var name = Path.GetFileNameWithoutExtension(movie.Filename);
			if (string.IsNullOrEmpty(name)) name = Game.FilesystemSafeName();
			var suggested = Path.Combine(Config.PathEntries.AvAbsolutePath(), $"{name}.{extension}");

			_encodeDialog = new EncodeVideoForm(
				markers,
				CurrentCaptureSize(),
				Config,
				suggested,
				BeginVideoEncode,
				CancelVideoEncode,
				() => EncodeProgress,
				current => this.ShowFileSaveDialog(
					filter: FilesystemFilterSet.Screenshots,
					initDir: Path.GetDirectoryName(current) ?? Config.PathEntries.AvAbsolutePath(),
					initFileName: Path.GetFileName(current)),
				confirmOverwrite: path => this.ModalMessageBox2(
					caption: "Replace the video?",
					icon: EMsgBoxIcon.Warning,
					text: $"{Path.GetFileName(path)} already exists. Replace it with this encode?"));
			_encodeDialog.Show(this);
		}

		/// <summary>
		/// The size a frame would be captured at right now, which is what the dialog
		/// offers as the output size. Worked out the way the A/V writer chooser has
		/// always worked it out.
		/// </summary>
		private Size CurrentCaptureSize()
		{
			if (Config.AviCaptureOsd)
			{
				using var captured = CaptureOSD();
				return new(captured.Width, captured.Height);
			}

			return Emulator.HasVideoProvider()
				? new(Emulator.AsVideoProvider().BufferWidth, Emulator.AsVideoProvider().BufferHeight)
				: new(640, 480);
		}

		/// <summary>
		/// Starts an encode. Returns null when it began, or a sentence saying why
		/// it could not - the dialog shows that and stays open.
		/// </summary>
		public string? BeginVideoEncode(VideoEncodeRequest request)
		{
			if (_encode is not null) return "An encode is already running.";
			if (Game.IsNullInstance()) return "No game is loaded.";
			if (MovieSession.Movie is not ITasMovie movie || movie.NotActive())
				return "No movie is loaded. A video is an encode of a run.";
			if (!Emulator.HasSavestates())
				return "This core cannot save states, so a run cannot be wound to a starting frame.";
			if (request.StartFrame < 0 || request.EndFrame < request.StartFrame)
				return "The end marker is before the start marker.";
			if (request.EndFrame >= movie.InputLogLength)
				return $"The movie ends at frame {movie.InputLogLength - 1}.";
			if (string.IsNullOrWhiteSpace(request.OutputPath))
				return "Choose a file to write the video to.";
			if (string.IsNullOrWhiteSpace(request.FFmpegCommand))
				return "The ffmpeg command is empty.";
			if (!FFmpegService.QueryServiceAvailable()) return FFmpegService.MissingMessage;

			var directory = Path.GetDirectoryName(request.OutputPath);
			if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
				return $"There is no folder {directory}.";

			// The capture settings are the ones the A/V writer has always read out
			// of the config, so the dialog writes the person's choices there rather
			// than inventing a second set that would disagree with the first.
			(Config.AVWriterResizeWidth, Config.AVWriterResizeHeight) = request.Width > 0 && request.Height > 0
				? (request.Width, request.Height)
				: (-1, -1);
			Config.AVWriterPad = request.Pad;
			Config.VideoWriterAudioSyncEffective = Config.VideoWriterAudioSync = request.AudioSync;
			Config.AviCaptureOsd = request.CaptureOsd;
			Config.AviCaptureLua = request.CaptureLua || request.CaptureOsd;

			MemoryStream restore = new();
			Emulator.AsStatable().SaveStateBinary(new BinaryWriter(restore));
			restore.Position = 0;

			// Whether it was RECORDING is taken here, before the switch below turns
			// it off: it is one of the things the encode borrows and has to give
			// back.
			_encode = new(request, restore, MovieSession.ReadOnly, EmulatorPaused, movie.IsRecording());

			// A reproduction, not an authoring session: the movie plays, nothing is
			// recorded over it, and no state is kept from the frames that go by.
			MovieSession.ReadOnly = true;
			MovieSession.SuppressStateCapture = true;
			if (movie.IsRecording()) movie.SwitchToPlay();

			// Wind to the first frame. Frame zero always has a state, so this never
			// needs to reboot the core - the worst case is replaying from the start.
			try
			{
				var landed = movie.States.Nearest(request.StartFrame);
				if (landed < 0 || !movie.States.RestoreTo(landed))
				{
					throw new InvalidOperationException("the state history has no frame at or before it");
				}
				if (landed is 0 && movie.StartsFromSavestate) Emulator.ResetCounters();
			}
			catch (Exception e)
			{
				FinishVideoEncode(VideoEncodePhase.Failed, $"Could not wind the run to frame {request.StartFrame}: {e.Message}");
				return _encode?.Message ?? "Could not wind the run to the starting frame.";
			}

			UnpauseEmulator();
			AddOnScreenMessage("Encoding video");

			// Already there: nothing to seek, so the file opens now and the very
			// next frame the loop runs is the first one in the video.
			if (Emulator.Frame >= request.StartFrame)
			{
				var error = OpenEncodeFile();
				if (error is not null) return error;
			}

			return null;
		}

		/// <summary>
		/// The same encode, asked for in one line, for a script or a test. The
		/// picture is whatever the config says it is - a script that wanted a
		/// particular size would have set it - so only the answers a caller cannot
		/// guess are parameters.
		/// </summary>
		public string? BeginVideoEncode(string path, int fromFrame, int toFrame, string ffmpegCommand)
			=> BeginVideoEncode(new VideoEncodeRequest
			{
				OutputPath = path,
				FFmpegCommand = ffmpegCommand,
				StartFrame = fromFrame,
				EndFrame = toFrame,
				Width = Math.Max(0, Config.AVWriterResizeWidth),
				Height = Math.Max(0, Config.AVWriterResizeHeight),
				Pad = Config.AVWriterPad,
				AudioSync = Config.VideoWriterAudioSyncEffective,
				CaptureOsd = Config.AviCaptureOsd,
				CaptureLua = Config.AviCaptureLua,
			});

		/// <summary>Stops an encode early. What was written stays written, and plays.</summary>
		public void CancelVideoEncode()
		{
			if (_encode is null) return;
			FinishVideoEncode(VideoEncodePhase.Stopped,
				$"Stopped after {_encode.FramesDone} of {_encode.Request.FrameCount} frames.");
		}

		/// <summary>
		/// Where the encode has got to, for the dialog to draw. A finished job keeps
		/// answering after the run loop has let go of it, so the dialog can say how
		/// it went rather than falling silent the moment it ends.
		/// </summary>
		public VideoEncodeProgress EncodeProgress
			=> (_encode ?? _lastEncode)?.Snapshot(Emulator.Frame)
				?? new(VideoEncodePhase.Idle, 0, 0, 0, 0, null, null);

		/// <summary>
		/// Called by the run loop once a frame has been emulated and dumped. The
		/// dumping already happened in <see cref="AvFrameAdvance"/>; this only
		/// counts it and decides whether the run has reached the end.
		/// </summary>
		private void EncodeAfterFrame()
		{
			if (_encode is null || !_encode.IsRunning) return;

			if (!_encode.IsEncoding)
			{
				// still winding: nothing is being written, so nothing to count
				if (Emulator.Frame >= _encode.Request.StartFrame) OpenEncodeFile();
				return;
			}

			_encode.CountFrame();
			if (Emulator.Frame > _encode.Request.EndFrame)
			{
				FinishVideoEncode(VideoEncodePhase.Done,
					$"Wrote {_encode.FramesDone} frames to {Path.GetFileName(_encode.Request.OutputPath)}.");
			}
		}

		/// <summary>
		/// Opens the output file and starts the writer. Returns null on success, or
		/// the reason - which also ends the job, because an encode that cannot write
		/// anywhere is over.
		/// </summary>
		private string? OpenEncodeFile()
		{
			if (_encode is null) return null;
			var request = _encode.Request;
			try
			{
				var preset = FFmpegWriterForm.FormatPreset.CustomPreset(request.FFmpegCommand);
				IVideoWriter writer = new FFmpegWriter(this);
				writer = Config.VideoWriterAudioSyncEffective ? new VideoStretcher(writer) : new AudioStretcher(writer);
				writer.SetMovieParameters(Emulator.VsyncNumerator(), Emulator.VsyncDenominator());
				(IVideoProvider output, Action? dispose) = GetCaptureProvider();
				writer.SetVideoParameters(output.BufferWidth, output.BufferHeight);
				dispose?.Invoke();
				writer.SetAudioParameters(44100, 2, 16);
				writer.SetVideoCodecToken(preset);
				writer.OpenFile(request.OutputPath);
				_currAviWriter = writer;
			}
			catch (Exception e)
			{
				var reason = $"Could not start writing {Path.GetFileName(request.OutputPath)}: {e.Message}";
				FinishVideoEncode(VideoEncodePhase.Failed, reason);
				return reason;
			}

			ConfigureAvSound();
			_encode.StartEncoding();
			return null;
		}

		/// <summary>
		/// Closes the file and gives the emulator back: the frame the person was on,
		/// the movie mode they had, their pause, their sound.
		/// </summary>
		private void FinishVideoEncode(VideoEncodePhase phase, string? message)
		{
			if (_encode is null) return;
			var job = _encode;

			// cleared first: closing the writer and reloading the state both step
			// through code that asks whether an encode is running
			_encode = null;

			if (_currAviWriter is not null) StopAv();

			MovieSession.SuppressStateCapture = false;
			MovieSession.ReadOnly = job.WasReadOnly;

			try
			{
				job.RestoreState.Position = 0;
				Emulator.AsStatable().LoadStateBinary(new BinaryReader(job.RestoreState));
			}
			catch (Exception e)
			{
				message = $"{message} The emulator could not be put back where it was ({e.Message}); load a state.";
			}
			finally
			{
				job.RestoreState.Dispose();
			}

			// The movie mode, which the summary above has always promised to give
			// back and nothing actually did (issue #49). BeginVideoEncode switches a
			// recording movie to Play so the encode reproduces the run instead of
			// recording over it, and the state load above cannot undo that: a mode
			// is only restored for a state that arrives through
			// MovieSession.HandleLoadState, and this one goes straight to the core.
			// Left in Play the piano roll takes no input at all, which is why
			// loading a branch or a savestate - both of which DO go through
			// HandleLoadState - appeared to repair it.
			if (job.WasRecording && MovieSession.Movie.IsActive() && !MovieSession.Movie.IsRecording())
			{
				MovieSession.Movie.SwitchToRecord();
			}

			if (job.WasPaused) PauseEmulator();
			Tools.UpdateToolsBefore();
			UpdateToolsAfter();

			job.Finish(phase, message);
			_lastEncode = job;
			AddOnScreenMessage(phase switch
			{
				VideoEncodePhase.Done => "Video encoded",
				VideoEncodePhase.Stopped => "Encoding stopped",
				_ => "Encoding failed",
			});
		}

		/// <summary>
		/// The finished job, so <see cref="EncodeProgress"/> can still say how it
		/// went after the run loop has let go of it.
		/// </summary>
		private VideoEncodeJob? _lastEncode;
	}
}
