using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using Chimera.Client.Common;
using Chimera.Client.GUI.ToolExtensions;
using Chimera.Common;
using Chimera.Common.CollectionExtensions;
using Chimera.Common.StringExtensions;
using Chimera.Emulation.Common;

namespace Chimera.Client.GUI
{
	public partial class TAStudio
	{
		private static readonly FilesystemFilterSet MoviesFSFilterSet = new(
			FilesystemFilter.TAStudioProjects);

		/// <summary>
		/// Load the movie with the given filename within TAStudio.
		/// </summary>
		public bool LoadMovieFile(string filename, bool askToSave = true)
		{
			if (askToSave && !AskSaveChanges()) return false;
			if (filename.EndsWithOrdinal(MovieService.TasMovieExtension))
			{
				return LoadFileWithFallback(filename);
			}
			DialogController.ShowMessageBox(
				caption: "Movie load error",
				icon: EMsgBoxIcon.Error,
				text: "This is not a Chimera movie!");
			return false;
		}

		// ---- saving: the menu items live on the MAIN window ----------------------
		// What is being saved is the PROJECT, and the project is the main window's
		// (File > Save Project). These are what those items call.

		/// <summary>Whether there is anything to save - the Save Project item's condition.</summary>
		public bool HasUnsavedChanges => CurrentTasMovie?.Changes is true;

		/// <summary>
		/// Whether a backup means anything yet: a project that has never been
		/// written has nothing to back up but its own default name.
		/// </summary>
		public bool CanSaveBackup
			=> !string.IsNullOrWhiteSpace(CurrentTasMovie?.Filename)
				&& CurrentTasMovie.Filename != DefaultTasProjName();

		public void SaveProject()
		{
			DisplayMessageIfFailed(() => SaveTas(), "Failed to save the project.");
			if (Settings.BackupPerFileSave)
			{
				DisplayMessageIfFailed(() => SaveTas(saveBackup: true), "Failed to save backup.");
			}
		}

		public void SaveProjectAs()
		{
			DisplayMessageIfFailed(() => SaveAsTas(), "Failed to save the project.");
			if (Settings.BackupPerFileSave)
			{
				DisplayMessageIfFailed(() => SaveTas(saveBackup: true), "Failed to save backup.");
			}
		}

		public void SaveProjectBackup()
			=> DisplayMessageIfFailed(() => SaveTas(saveBackup: true), "Failed to save backup.");

		private void SaveSelectionToMacroMenuItem_Click(object sender, EventArgs e)
		{
			if (!AnyRowsSelected)
			{
				return;
			}

			int endIndex = LastSelectedRowIndex;
			if (LastSelectedRowIndex == CurrentTasMovie.InputLogLength)
			{
				endIndex--;
			}

			var file = SaveFileDialog(
				null,
				MacroInputTool.SuggestedFolder(Config, Game),
				MacroInputTool.MacrosFSFilterSet,
				this
			);

			if (file != null)
			{
				var selectionStart = FirstSelectedRowIndex;
				MovieZone macro = new(
					CurrentTasMovie,
					start: selectionStart,
					length: endIndex - selectionStart + 1);
				FileWriteResult saveResult = macro.Save(file.FullName);

				if (saveResult.IsError)
				{
					DialogController.ShowMessageBox(
						$"Failed to save macro.\n{saveResult.UserFriendlyErrorMessage()}\n{saveResult.Exception.Message}",
						"Error",
						EMsgBoxIcon.Error);
				}
				else
				{
					Config.RecentMacros.Add(file.FullName);
				}
			}
		}

		private void PlaceMacroAtSelectionMenuItem_Click(object sender, EventArgs e)
		{
			if (!AnyRowsSelected)
			{
				return;
			}

			var file = OpenFileDialog(
				null,
				MacroInputTool.SuggestedFolder(Config, Game),
				MacroInputTool.MacrosFSFilterSet
			);

			if (file != null)
			{
				DummyLoadMacro(file.FullName);
				Config.RecentMacros.Add(file.FullName);
			}
		}

		private void RecentMacrosMenuItem_DropDownOpened(object sender, EventArgs e)
			=> recentMacrosToolStripMenuItem.ReplaceDropDownItems(Config!.RecentMacros.RecentMenu(this, DummyLoadMacro, "Macro", noAutoload: true));

		private void EditSubMenu_DropDownClosed(object sender, EventArgs e)
		{
			// These specific menu items have their ShortcutKeys property set.
			// These are not user-configurable hotkeys handled by Chimera's hotkey system.
			// They are andled by .NET. So they must be enabled in order to work!
			// (We disable them in EditSubMenu_DropDownOpened if there is no selection.)
			CopyMenuItem.Enabled = true;
			CutMenuItem.Enabled = true;
			PasteMenuItem.Enabled = true;
			PasteInsertMenuItem.Enabled = true;
		}

		private void EditSubMenu_DropDownOpened(object sender, EventArgs e)
		{
			// the macro items work on the selection too (they moved here when
			// TAStudio's File menu went to the main window)
			saveSelectionToMacroToolStripMenuItem.Enabled =
				placeMacroAtSelectionToolStripMenuItem.Enabled =
				recentMacrosToolStripMenuItem.Enabled =
				AnyRowsSelected;

			DeselectMenuItem.Enabled =
				SelectBetweenMarkersMenuItem.Enabled =
				CopyMenuItem.Enabled =
				CutMenuItem.Enabled =
				ClearFramesMenuItem.Enabled =
				DeleteFramesMenuItem.Enabled =
				CloneFramesMenuItem.Enabled =
				CloneFramesXTimesMenuItem.Enabled =
				TruncateMenuItem.Enabled =
				InsertFrameMenuItem.Enabled =
				InsertNumFramesMenuItem.Enabled =
				AnyRowsSelected;

			ReselectClipboardMenuItem.Enabled =
				PasteMenuItem.Enabled =
				PasteInsertMenuItem.Enabled = AnyRowsSelected
					&& Clipboard.ContainsText();

			ClearGreenzoneMenuItem.Enabled =
				CurrentTasMovie?.States is { Count: > 1 };

			GreenzoneICheckSeparator.Visible =
				StateHistoryIntegrityCheckMenuItem.Visible =
				true;

			UndoMenuItem.Enabled = CurrentTasMovie.ChangeLog.CanUndo;
			RedoMenuItem.Enabled = CurrentTasMovie.ChangeLog.CanRedo;
		}

		private void UndoMenuItem_Click(object sender, EventArgs e)
		{
			CurrentTasMovie.ChangeLog.Undo();
		}

		private void RedoMenuItem_Click(object sender, EventArgs e)
		{
			CurrentTasMovie.ChangeLog.Redo();
		}

		private void ShowUndoHistoryMenuItem_Click(object sender, EventArgs e)
		{
			if (_undoForm != null && !_undoForm.IsDisposed)
			{
				// We could just BringToFront, but closing is probably better since the new one will appear in the expected screen location.
				_undoForm.Close();
			}

			_undoForm = new UndoHistoryForm(this) { Owner = this };
			if (Settings.UndoHistoryWindow.Width != 0)
			{
				_undoForm.Location = Settings.UndoHistoryWindow.Location;
				_undoForm.Size = Settings.UndoHistoryWindow.Size;
			}
			_undoForm.FormClosed += (s, e) => Settings.UndoHistoryWindow = new(_undoForm.Location, _undoForm.Size);
			_undoForm.Show();
			_undoForm.UpdateValues();
		}

		private void DeselectMenuItem_Click(object sender, EventArgs e)
		{
			_activeInputRoll.DeselectAll();
			_activeInputRoll.Refresh();
		}

		/// <remarks>TODO merge w/ Deselect?</remarks>
		private void SelectAllMenuItem_Click(object sender, EventArgs e)
		{
			_activeInputRoll.SelectAll();
			_activeInputRoll.Refresh();
		}

		private void SelectBetweenMarkersMenuItem_Click(object sender, EventArgs e)
		{
			if (AnyRowsSelected)
			{
				var selectionEnd = LastSelectedRowIndex;
				var prevMarker = CurrentTasMovie.Markers.PreviousOrCurrent(selectionEnd);
				var nextMarker = CurrentTasMovie.Markers.Next(selectionEnd);

				int prev = prevMarker?.Frame ?? 0;
				int next = nextMarker?.Frame ?? CurrentTasMovie.InputLogLength;

				_activeInputRoll.DeselectAll();
				for (int i = prev; i < next; i++)
				{
					_activeInputRoll.SelectRow(i, true);
				}
				_activeInputRoll.Refresh();

				SetSplicer();
			}
		}

		private void ReselectClipboardMenuItem_Click(object sender, EventArgs e)
		{
			_activeInputRoll.DeselectAll();
			foreach (var item in _tasClipboard)
			{
				_activeInputRoll.SelectRow(item.Frame, true);
			}
			_activeInputRoll.Refresh();

			SetSplicer();
		}

		private void GoToFrameMenuItem_Click(object sender, EventArgs e)
		{
			MainForm.PauseEmulator();
			using InputPrompt dialog = new()
			{
				Text = "Go to Frame",
				Message = "Jump/Seek to frame index:",
				TextInputType = InputPrompt.InputType.Unsigned,
			};
			if (this.ShowDialogWithTempMute(dialog).IsOk()) GoToFrame(int.Parse(dialog.PromptText));
		}

		private void CopyMenuItem_Click(object sender, EventArgs e)
		{
			if (AnyRowsSelected)
			{
				_tasClipboard.Clear();
				var list = GetSelection().ToArray();
				var sb = new StringBuilder();

				foreach (var index in list)
				{
					var input = CurrentTasMovie.GetInputState(index);
					if (input == null)
					{
						break;
					}

					_tasClipboard.Add(new TasClipboardEntry(index, input));
					var logEntry = LogEntryGenerator.GenerateLogEntry(input);
					sb.AppendLine(Settings.CopyIncludesFrameNo ? $"{FrameToStringPadded(index)} {logEntry}" : logEntry);
				}

				Clipboard.SetDataObject(sb.ToString());
				SetSplicer();
			}
		}

		private void PasteMenuItem_Click(object sender, EventArgs e)
		{
			// TODO: if highlighting 2 rows and pasting 3, only paste 2 of them
			// FCEUX Taseditor doesn't do this, but I think it is the expected behavior in editor programs
			MaybePasteFromClipboard(overwriteSelection: true);
		}

		private void PasteInsertMenuItem_Click(object sender, EventArgs e)
			=> MaybePasteFromClipboard(overwriteSelection: false);

		private void MaybePasteFromClipboard(bool overwriteSelection)
		{
			if (!AnyRowsSelected) return;
			var input = Clipboard.GetText();
			if (string.IsNullOrWhiteSpace(input)) return;
			string[] lines = input.Split('\n');
			if (lines.Length is 0) return;

			_tasClipboard.Clear();
			int linesToPaste = lines.Length;
			if (lines[lines.Length - 1].Length is 0) linesToPaste--;
			for (int i = 0; i < linesToPaste; i++)
			{
				var line = ControllerFromMnemonicStr(lines[i]);
				if (line == null)
				{
					return;
				}

				// remove inputs not in _visibleDefintion
				_tasClipboard.Add(new TasClipboardEntry(i, line));
			}

			var selectionStart = FirstSelectedRowIndex;
			var inputStates = _tasClipboard.Select(static x => x.ControllerState);
			if (overwriteSelection) CurrentTasMovie.CopyOverInput(selectionStart, inputStates);
			else CurrentTasMovie.InsertInput(selectionStart, inputStates);
		}

		private void CutMenuItem_Click(object sender, EventArgs e)
		{
			if (AnyRowsSelected)
			{
				_tasClipboard.Clear();
				var list = GetSelection().ToArray();
				var sb = new StringBuilder();

				foreach (var index in list) // copy of CopyMenuItem_Click()
				{
					var input = CurrentTasMovie.GetInputState(index);
					if (input == null)
					{
						break;
					}

					_tasClipboard.Add(new TasClipboardEntry(index, input));
					sb.AppendLine(LogEntryGenerator.GenerateLogEntry(input));
				}

				Clipboard.SetDataObject(sb.ToString());
				CurrentTasMovie.RemoveFrames(list);
				SetSplicer();
			}
		}

		private void ClearFramesMenuItem_Click(object sender, EventArgs e)
		{
			if (!AnyRowsSelected) return;

			CurrentTasMovie.SingleInvalidation(() =>
			{
				CurrentTasMovie.ChangeLog.BeginNewBatch($"Clear frames {FirstSelectedRowIndex}-{LastSelectedRowIndex}");
				foreach (int frame in GetSelection())
				{
					CurrentTasMovie.ClearFrame(frame);
				}

				CurrentTasMovie.ChangeLog.EndBatch();
			});
		}

		private void DeleteFramesMenuItem_Click(object sender, EventArgs e)
		{
			if (AnyRowsSelected)
			{
				var rollBackFrame = FirstSelectedRowIndex;
				if (rollBackFrame >= CurrentTasMovie.InputLogLength)
				{
					// Cannot delete non-existent frames
					RefreshDialog();
					return;
				}

				CurrentTasMovie.RemoveFrames(GetSelection().ToArray());
				SetTasViewRowCount();
				SetSplicer();
			}
		}

		private void CloneFramesMenuItem_Click(object sender, EventArgs e)
		{
			CloneFramesXTimes(1);
		}

		private void CloneFramesXTimesMenuItem_Click(object sender, EventArgs e)
		{
			using var framesPrompt = new FramesPrompt("Clone # Times", "Insert times to clone:");
			if (framesPrompt.ShowDialogOnScreen().IsOk())
			{
				CloneFramesXTimes(framesPrompt.Frames);
			}
		}

		private void CloneFramesXTimes(int timesToClone)
		{
			if (!AnyRowsSelected) return;

			var framesToInsert = GetSelection();
			var insertionFrame = Math.Min(LastSelectedRowIndex + 1, CurrentTasMovie.InputLogLength);

			// Get controller instead of string log
			var inputLog = framesToInsert
				.Select(CurrentTasMovie.GetInputLogEntry)
				.ToList();

			CurrentTasMovie.SingleInvalidation(() =>
			{
				string batchName = $"Clone {inputLog.Count} frames starting at {FirstSelectedRowIndex}";
				if (timesToClone != 1) batchName += $" {timesToClone} times";
				CurrentTasMovie.ChangeLog.BeginNewBatch(batchName);

				for (int i = 0; i < timesToClone; i++)
				{
					CurrentTasMovie.InsertInput(insertionFrame, inputLog);
				}

				CurrentTasMovie.ChangeLog.EndBatch();
			});
		}

		private void InsertFrameMenuItem_Click(object sender, EventArgs e)
		{
			if (AnyRowsSelected)
			{
				CurrentTasMovie.InsertEmptyFrame(FirstSelectedRowIndex);
			}
		}

		private void InsertNumFramesMenuItem_Click(object sender, EventArgs e)
		{
			if (AnyRowsSelected)
			{
				var insertionFrame = FirstSelectedRowIndex;
				using var framesPrompt = new FramesPrompt();
				if (framesPrompt.ShowDialogOnScreen().IsOk())
				{
					CurrentTasMovie.InsertEmptyFrame(insertionFrame, framesPrompt.Frames);
				}
			}
		}

		private void TruncateMenuItem_Click(object sender, EventArgs e)
		{
			if (AnyRowsSelected)
			{
				CurrentTasMovie.Truncate(LastSelectedRowIndex);
				MarkerControl.MarkerInputRoll.TruncateSelection(CurrentTasMovie.Markers.Count - 1);
			}
		}

		private void SetMarkersMenuItem_Click(object sender, EventArgs e)
		{
			var selectedRows = GetSelection().ToList();
			if (selectedRows.Count > 50)
			{
				var result = DialogController.ShowMessageBox2("Are you sure you want to add more than 50 markers?", "Add markers", EMsgBoxIcon.Question, useOKCancel: true);
				if (!result)
				{
					return;
				}
			}

			foreach (var index in selectedRows)
			{
				MarkerControl.AddMarker(index);
			}
		}

		private void SetMarkerWithTextMenuItem_Click(object sender, EventArgs e)
		{
			MarkerControl.AddMarker(AnyRowsSelected ? FirstSelectedRowIndex : 0, true);
		}

		private void RemoveMarkersMenuItem_Click(object sender, EventArgs e)
		{
			CurrentTasMovie.Markers.RemoveAll(m => IsRowSelected(m.Frame));
			MarkerControl.UpdateMarkerCount();
		}

		/// <summary>
		/// Throws away every savestate the run has accumulated and goes back to
		/// frame 0, which is the only way to make the machine prove itself: from
		/// there the movie replays from nothing, and a desync has nowhere to hide
		/// behind a state that was saved before it happened.
		///
		/// That matters most on a hardware renderer, where the picture comes from
		/// a GPU outside the savestate and sync is a thing to check rather than
		/// assume - which is what the New Project window says when one is chosen.
		/// Frame 0's state survives the clear (it is reserved), so this is a load
		/// rather than a reboot.
		/// </summary>
		public void ClearGreenzone()
		{
			CurrentTasMovie.States.InvalidateAfter(0);   // everything but the anchor
			GoToFrame(0);
			RefreshDialog();
		}

		/// <summary>
		/// The menu item, and the Lua binding beside it (tastudio.cleargreenzone),
		/// are the same action: a crash that only happens after the greenzone is
		/// thrown away has no other way to be reproduced unattended, and the soak
		/// script is where that reproduction has to live.
		/// </summary>
		private void ClearGreenzoneMenuItem_Click(object sender, EventArgs e) => ClearGreenzone();

		private void StateHistoryIntegrityCheckMenuItem_Click(object sender, EventArgs e)
		{
			if (!Emulator.DeterministicEmulation)
			{
				if (!DialogController.ShowMessageBox2("The emulator is not deterministic. It might fail even if the difference isn't enough to cause a desync.\nContinue with check?", "Not Deterministic"))
				{
					return;
				}
			}

			GoToFrame(0);
			int lastState = 0;
			int goToFrame = CurrentTasMovie.States.Nearest(int.MaxValue);
			if (goToFrame <= 0)
			{
				DialogController.ShowMessageBox("The state history holds nothing to check yet.", "Integrity Check");
				return;
			}
			do
			{
				MainForm.FrameAdvance();

				if (CurrentTasMovie.States.Has(Emulator.Frame))
				{
					// The history no longer holds a frame as bytes - most frames
					// are reached by walking deltas from an anchor - so the check
					// is "does it put me back where I am": save the machine, ask
					// the history for this frame, save again, compare. It lands on
					// the frame it started on either way, so the walk goes on.
					byte[] state = StatableEmulator.CloneSavestate();
					CurrentTasMovie.States.RestoreTo(Emulator.Frame);
					byte[] greenZone = StatableEmulator.CloneSavestate();

					if (!state.SequenceEqual(greenZone))
					{
						if (DialogController.ShowMessageBox2($"Bad data between frames {lastState} and {Emulator.Frame}. Save the relevant state (raw data)?", "Integrity Failed!"))
						{
							var result = this.ShowFileSaveDialog(initDir: Config!.PathEntries.ToolsAbsolutePath(), initFileName: "integrity.fresh");
							if (result is not null)
							{
								File.WriteAllBytes(result, state);
								var path = Path.ChangeExtension(result, ".greenzoned");
								File.WriteAllBytes(path, greenZone);
							}
						}

						return;
					}

					lastState = Emulator.Frame;
				}
			}
			while (Emulator.Frame < goToFrame);

			DialogController.ShowMessageBox("Integrity Check passed");
		}

		public void UpdateChangeLogMaxSteps(int value)
		{
			Settings.MaxUndoSteps = value;
			CurrentTasMovie.ChangeLog.MaxSteps = value;
			BookMarkControl.SetBackupMaxSteps(value);
			foreach (TasBranch branch in CurrentTasMovie.Branches)
			{
				branch.ChangeLog.MaxSteps = value;
			}

			if (_undoForm != null && !_undoForm.IsDisposed)
			{
				_undoForm.UpdateValues();
			}
		}

		private void CopyIncludesFrameNoMenuItem_Click(object sender, EventArgs e)
			=> Settings.CopyIncludesFrameNo = !Settings.CopyIncludesFrameNo;

		private void HeaderMenuItem_Click(object sender, EventArgs e)
		{
			using MovieHeaderEditor form = new(CurrentTasMovie, Config)
			{
				Owner = this,
				Location = this.ChildPointToScreen(_inputRolls[0]),
			};
			form.ShowDialogOnScreen();
		}

		private void CommentsMenuItem_Click(object sender, EventArgs e)
		{
			using EditCommentsForm form = new(CurrentTasMovie, false)
			{
				Owner = this,
				StartPosition = FormStartPosition.Manual,
				Location = this.ChildPointToScreen(_inputRolls[0]),
			};
			form.ShowDialogOnScreen();
		}

		private void SubtitlesMenuItem_Click(object sender, EventArgs e)
		{
			using EditSubtitlesForm form = new(
				DialogController,
				CurrentTasMovie,
				Config!.PathEntries,
				readOnly: false)
			{
				Owner = this,
				StartPosition = FormStartPosition.Manual,
				Location = this.ChildPointToScreen(_inputRolls[0]),
			};
			form.ShowDialogOnScreen();
		}

		private void SetUpToolStripColumns()
		{
			SetUpToolStripColumns(ShowColumnsContextMenuItem);
			SetUpToolStripColumns(ColumnsSubMenu);
			ToolStripMenuItem rollIndicatorItem = new();
			ColumnsSubMenu.DropDownItems.Insert(0, rollIndicatorItem);
			ColumnsSubMenu.DropDownOpening += (_, _) => {
				rollIndicatorItem.Text = $"Current roll: #{_inputRolls.IndexOf(_activeInputRoll) + 1}";
				rollIndicatorItem.Visible = _inputRolls.Count > 1;
			};
		}

		private void SetUpToolStripColumns(ToolStripMenuItem parent)
		{
			parent.DropDownItems.Clear();

			var columns = _inputRolls[0].AllColumns
				.Where(static c => !string.IsNullOrWhiteSpace(c.Text) && c.Name is not FrameColumnName)
				.ToList();

			int workingHeight = Screen.FromControl(this).WorkingArea.Height;
			int rowHeight = parent.Height + 4;
			int maxRows = workingHeight / rowHeight;
			int keyCount = columns.Count(c => c.Name.StartsWithOrdinal("Key "));
			int keysMenusCount = (int)Math.Ceiling((double)keyCount / maxRows);

			// Groupings for keys (we just have a lot of them) and for players
			var keysMenus = new ToolStripMenuItem[keysMenusCount];
			for (int i = 0; i < keysMenus.Length; i++)
			{
				keysMenus[i] = new ToolStripMenuItem();
				parent.DropDownItems.Add(keysMenus[i]);
			}

			var playerMenus = new ToolStripMenuItem[Emulator.ControllerDefinition.ControlsOrdered.Count];
			playerMenus[0] = parent;
			for (int i = 1; i < playerMenus.Length; i++)
			{
				playerMenus[i] = new ToolStripMenuItem($"Player {i}");
			}

			bool programmaticallyHidingColumns = false;
			void AfterCheckChange(ToolStripMenuItem item)
			{
				if (!programmaticallyHidingColumns)
				{
					_activeInputRoll.AllColumns.ColumnsChanged();
					CurrentTasMovie.FlagChanges();
					_activeInputRoll.Refresh();

					UpdateInputRollDefinition(_activeInputRoll);

					if (item.OwnerItem != parent) parent.ShowDropDown();
					((ToolStripMenuItem)item.OwnerItem).ShowDropDown();
				}
			}

			// Items for individual columns
			foreach (var column in columns)
			{
				var menuItem = new ToolStripMenuItem
				{
					Text = $"{column.Text} ({column.Name})",
					Checked = column.Visible,
					CheckOnClick = true,
					Tag = column.Name,
				};

				menuItem.CheckedChanged += (o, ev) =>
				{
					ToolStripMenuItem sender = (ToolStripMenuItem)o;
					_activeInputRoll.AllColumns.Find(c => c.Name == (string)sender.Tag).Visible = sender.Checked;
					AfterCheckChange(sender);
				};

				if (column.Name.StartsWithOrdinal("Key "))
				{
					keysMenus
						.First(m => m.DropDownItems.Count < maxRows)
						.DropDownItems
						.Add(menuItem);
				}
				else
				{
					int player;

					if (column.Name.Length >= 2 && column.Name.StartsWith('P') && char.IsNumber(column.Name, 1))
					{
						player = int.Parse(column.Name[1].ToString());
					}
					else
					{
						player = 0;
					}

					playerMenus[player].DropDownItems.Add(menuItem);
				}
			}
			// update text on key group dropdowns
			foreach (var menu in keysMenus)
			{
				string text = $"Keys ({menu.DropDownItems[0].Tag} - {menu.DropDownItems[menu.DropDownItems.Count - 1].Tag})";
				menu.Text = text.Replace("Key ", "");
			}
			// add player menus only if they actually contain items
			foreach (ToolStripMenuItem menu in playerMenus.Skip(1).Where(static m => m.HasDropDownItems))
			{
				parent.DropDownItems.Add(menu);
			}

			parent.DropDownItems.Add(new ToolStripSeparator());

			// button for the group of all keys
			if (keysMenus.Length > 0)
			{
				ToolStripMenuItem item = new("Show Keys") { CheckOnClick = true };

				// Handle updating the check state when individual keys are toggled
				parent.DropDownOpening += (_, _) =>
				{
					if (programmaticallyHidingColumns) return;
					programmaticallyHidingColumns = true;
					item.CheckState = keysMenus
						.SelectMany(static submenu => submenu.DropDownItems.OfType<ToolStripMenuItem>())
						.Select(static mi => mi.Checked).Unanimity().ToCheckState();
					programmaticallyHidingColumns = false;
				};

				// Handle checking the button for all keys
				item.CheckedChanged += (o, ev) =>
				{
					if (programmaticallyHidingColumns) return;
					programmaticallyHidingColumns = true;
					foreach (var menu in keysMenus)
					{
						foreach (ToolStripMenuItem menuItem in menu.DropDownItems)
						{
							menuItem.Checked = item.Checked;
						}
					}
					programmaticallyHidingColumns = false;

					AfterCheckChange(item);
				};
				parent.DropDownItems.Add(item);
			}

			// buttons for the groups of each players' columns
			void UpdateIndividualItems(ToolStripMenuItem menu)
			{
				foreach (ToolStripMenuItem item in menu.DropDownItems.OfType<ToolStripMenuItem>())
				{
					string/*?*/ tag = item.Tag as string;
					if (!string.IsNullOrEmpty(tag))
					{
						item.Checked = _activeInputRoll.AllColumns.Find(c => c.Name == tag).Visible;
					}
				}
			}
			for (int i = 1; i < playerMenus.Length; i++)
			{
				ToolStripMenuItem dummyObject = playerMenus[i];
				if (!dummyObject.HasDropDownItems) continue;
				ToolStripMenuItem item = new($"Show Player {i}") { CheckOnClick = true };
				dummyObject.DropDownOpening += (_, _) =>
				{
					programmaticallyHidingColumns = true;

					UpdateIndividualItems(dummyObject);
					item.CheckState = dummyObject.DropDownItems.OfType<ToolStripMenuItem>().Skip(1)
						.Select(static mi => mi.Checked).Unanimity().ToCheckState();

					programmaticallyHidingColumns = false;
				};
				item.CheckedChanged += (o, ev) =>
				{
					if (programmaticallyHidingColumns) return;
					programmaticallyHidingColumns = true;
					// TODO: preserve underlying button checked state and make this a master visibility control
					foreach (var menuItem in dummyObject.DropDownItems.OfType<ToolStripMenuItem>().Skip(1))
					{
						menuItem.Checked = item.Checked;
					}
					programmaticallyHidingColumns = false;

					AfterCheckChange(item);
				};

				dummyObject.DropDownItems.Insert(0, item);
				dummyObject.DropDownItems.Insert(1, new ToolStripSeparator());
			}
			playerMenus[0].DropDownOpening += (_,_) => // "player menu 0" is for buttons not associated with a player, and doesn't have a button for the entire group
			{
				programmaticallyHidingColumns = true;
				UpdateIndividualItems(playerMenus[0]);
				programmaticallyHidingColumns = false;
			};

			parent.DropDownItems.Add(new ToolStripMenuItem
			{
				Enabled = false,
				Text = "Change Peripherals...",
				ToolTipText = "Changing peripherals/players is done in the core's sync settings (if the core supports different peripherals)."
					+ "\nAs these can't be changed in the middle of a movie, you'll have to close the project, change the settings, and create a new one.",
			});
		}

		// ReSharper disable once UnusedMember.Local
		[RestoreDefaults]
		private void RestoreDefaults()
		{
			SetUpColumns();
			SetUpToolStripColumns();
			_inputRolls.ForEach(static r => r.Refresh());
			CurrentTasMovie.FlagChanges();

			MainVertialSplit.SplitterDistance = _defaultMainSplitDistance;
			BranchesMarkersSplit.SplitterDistance = _defaultBranchMarkerSplitDistance;
		}

		private void ColumnRightClickMenu_Opened(object sender, EventArgs e)
		{
			if (_clickedColumn == null) return;

			bool cursorOrFrame = _clickedColumn.Name is (CursorColumnName or FrameColumnName);
			ControllerDefinition cd = MovieSession.MovieController.Definition;
			AutoHoldContextMenuItem.Visible = !cursorOrFrame
				&& (cd.BoolButtons.Contains(_clickedColumn.Name) || cd.Axes.ContainsKey(_clickedColumn.Name));
			HideColumnContextMenuItem.Visible = !cursorOrFrame;

			DeleteInputRollContextMenuItem.Visible = _inputRolls.Count > 1;
		}

		private void AutoHoldContextMenuItem_Click(object sender, EventArgs e)
		{
			if (_clickedColumn == null) return;
			ToggleAutoFire(_clickedColumn);
			_activeInputRoll.Refresh();
		}

		private void HideColumnContextMenuItem_Click(object sender, EventArgs e)
		{
			if (_clickedColumn == null) return;
			_clickedColumn.Visible = false;
			_activeInputRoll.AllColumns.ColumnsChanged();
			CurrentTasMovie.FlagChanges();
			_activeInputRoll.Refresh();
		}

		private void NewInputRollContextMenuItem_Click(object sender, EventArgs e)
		{
			IEnumerable<string> defaultHidden = GetDefaultHiddenColums(_inputRolls[0]).Select(static r => r.Name);
			HashSet<string> hiddenCols = _inputRolls
				.SelectMany(static r => r.AllColumns.Where(static c => !c.Visible))
				.Select(static c => c.Name)
				.Where(n => !defaultHidden.Contains(n))
				.Distinct()
				.ToHashSet();

			int index = _inputRolls.IndexOf(_activeInputRoll);
			InputRoll roll = MakeInputRoll(index);
			roll.AllColumns.AddRange(_activeInputRoll.AllColumns.Select(static c => c.Clone()));

			// If any columns aren't visible, default to showing those instead of showing all.
			if (hiddenCols.Count != 0)
			{
				foreach (RollColumn col in roll.AllColumns)
				{
					col.Visible = hiddenCols.Contains(col.Name) || col.Name is (CursorColumnName or FrameColumnName);
				}
			}
			roll.AllColumns.ColumnsChanged();
			UpdateInputRollDefinition(roll);

			RefreshDialog();
		}

		private void DeleteInputRollContextMenuItem_Click(object sender, EventArgs e)
		{
			if (_inputRolls.Count == 1) return;

			_tasViewPanel.Controls.Remove(_activeInputRoll);
			_rollDefinitions.Remove(_activeInputRoll);
			int index = _inputRolls.IndexOf(_activeInputRoll);
			_inputRolls.RemoveAt(index);
			_activeInputRoll = _inputRolls[0];

			RepositionRolls();
		}

		private void RightClickMenu_Opened(object sender, EventArgs e)
		{
			SetMarkersContextMenuItem.Enabled =
				SelectBetweenMarkersContextMenuItem.Enabled =
				RemoveMarkersContextMenuItem.Enabled =
				DeselectContextMenuItem.Enabled =
				ClearContextMenuItem.Enabled =
				DeleteFramesContextMenuItem.Enabled =
				CloneContextMenuItem.Enabled =
				CloneXTimesContextMenuItem.Enabled =
				InsertFrameContextMenuItem.Enabled =
				InsertNumFramesContextMenuItem.Enabled =
				TruncateContextMenuItem.Enabled =
				AnyRowsSelected;

			pasteToolStripMenuItem.Enabled =
				pasteInsertToolStripMenuItem.Enabled =
					AnyRowsSelected && Clipboard.ContainsText();

			RemoveMarkersContextMenuItem.Enabled = CurrentTasMovie.Markers.Any(m => IsRowSelected(m.Frame)); // Disable the option to remove markers if no markers are selected (FCEUX does this).
			CancelSeekContextMenuItem.Enabled = SeekingTo != -1;
			BranchContextMenuItem.Visible = CurrentCell?.RowIndex == Emulator.Frame;
		}

		private void CancelSeekContextMenuItem_Click(object sender, EventArgs e)
		{
			StopSeeking();
		}

		private void BranchContextMenuItem_Click(object sender, EventArgs e)
		{
			BookMarkControl.Branch();
		}

		private void WindowSubMenu_DropDownOpened(object sender, EventArgs e)
			=> MoveWithMainWindowMenuItem.Checked = Settings.MoveWithMainWindow;

		private void MoveWithMainWindowMenuItem_Click(object sender, EventArgs e)
			=> Settings.MoveWithMainWindow = !Settings.MoveWithMainWindow;

		private void TAStudioSettingsToolStripMenuItem_Click(object sender, EventArgs e)
		{
			TAStudioSettingsForm settingsForm = new(new()
				{
					GeneralClientSettings = Settings,
					MovieSettings = _movieSettings,
				},
				MovieSession.MovieController.Definition,
				(s) =>
				{
					// settings objects are mutated by the settings form, but some still need to be handled
					for (int i = 0; i < _inputRolls.Count; i++)
					{
						_inputRolls[i].SuspendDrawing();
						_inputRolls[i].AlwaysScroll = Settings.FollowCursorAlwaysScroll;
						_inputRolls[i].Font = Settings.TasViewFont;
						_inputRolls[i].HorizontalOrientation = s.MovieSettings.HorizontalOrientation;
						_inputRolls[i].LagFramesToHide = s.MovieSettings.LagFramesToHide;
						_inputRolls[i].HideWasLagFrames = s.MovieSettings.HideWasLagFrames;
						_inputRolls[i].ScrollMethod = Settings.FollowCursorScrollMethod;
						_inputRolls[i].ScrollSpeed = Settings.ScrollSpeed;
						UpdateColumnWidths(_inputRolls[i]);
					}
					GenerateIcons();
					foreach (InputRoll roll in _inputRolls) roll.ResumeDrawing();

					UpdateAutoFire();

					UpdateChangeLogMaxSteps(Settings.MaxUndoSteps);
					CurrentTasMovie.BindMarkersToInput = Settings.BindMarkersToInput;

					UpdateActiveMovieInputs();
				}
			);
			if (Settings.SettingsWindow.Width != 0)
			{
				settingsForm.Location = Settings.SettingsWindow.Location;
				settingsForm.Size = Settings.SettingsWindow.Size;
			}
			settingsForm.ShowDialog(this);
			Settings.SettingsWindow = new(settingsForm.Location, settingsForm.Size);

			RefreshDialog();
		}
	}
}
